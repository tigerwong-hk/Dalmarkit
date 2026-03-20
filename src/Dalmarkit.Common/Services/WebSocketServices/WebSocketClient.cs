using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dalmarkit.Common.Dtos.RequestDtos;
using Dalmarkit.Common.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Dalmarkit.Common.Services.WebSocketServices.IWebSocketClient;
using static Dalmarkit.Common.Services.WebSocketServices.WebSocketClientEvents;

namespace Dalmarkit.Common.Services.WebSocketServices;

public class WebSocketClient : IWebSocketClient
{
    public const int PendingRequestsInitialCapacity = 503;

    private readonly IEventDispatcher _eventDispatcher;
    private readonly ConcurrentDictionary<object, TaskCompletionSource<string>> _pendingRequests = new(Environment.ProcessorCount, PendingRequestsInitialCapacity);
    private readonly WebSocketClientOptions _options;
    private readonly ILogger<WebSocketClient> _logger;

    private readonly CancellationTokenSource _disposalCts = new();

    private readonly Lock _connectionLock = new();
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

    private readonly Channel<WebSocketReceivedMessage<ReadOnlyMemory<byte>>> _receiveBinaryChannel;
    private readonly Channel<WebSocketReceivedMessage<JsonNode>> _receiveTextChannel;

    private static readonly HashSet<(WebSocketConnectionState From, WebSocketConnectionState To)> ValidConnectionStateTransitions =
    [
        (WebSocketConnectionState.Disconnected, WebSocketConnectionState.Connecting),
        (WebSocketConnectionState.Disconnected, WebSocketConnectionState.Reconnecting),
        (WebSocketConnectionState.Connecting, WebSocketConnectionState.Connected),
        (WebSocketConnectionState.Connecting, WebSocketConnectionState.Disconnected),
        (WebSocketConnectionState.Reconnecting, WebSocketConnectionState.Connected),
        (WebSocketConnectionState.Reconnecting, WebSocketConnectionState.Disconnected),
        (WebSocketConnectionState.Connected, WebSocketConnectionState.Disconnected),
        (WebSocketConnectionState.Connected, WebSocketConnectionState.Disconnecting),
        (WebSocketConnectionState.Disconnecting, WebSocketConnectionState.Disconnected),
        (WebSocketConnectionState.Disconnecting, WebSocketConnectionState.Connected),
    ];

    private CancellationTokenSource? _checkHealthCts;
    private CancellationTokenSource? _receiveMessageCts;

    private Task? _checkHealthTask;
    private Task? _receiveMessageTask;

    private ClientWebSocket? _clientWebSocket;

    private volatile int _isDisposed;
    private long _connectionId;
    private long _lastReceivedTimestampMilliseconds;
    private volatile int _reconnectAttempts;

    private int _connectionStateValue = (int)WebSocketConnectionState.Disconnected;
    private volatile Func<string>? _getWebSocketServerUrl;

    public bool IsConnectionConnected => ConnectionState == WebSocketConnectionState.Connected;
    public bool IsWebSocketConnected => _clientWebSocket?.State == WebSocketState.Open;
    public bool HasReachedMaxReconnectAttempts => _reconnectAttempts >= (_options.Reconnection?.MaxAttempts ?? 0);

    public ChannelReader<WebSocketReceivedMessage<ReadOnlyMemory<byte>>> BinaryMessageReader => _receiveBinaryChannel.Reader;
    public ChannelReader<WebSocketReceivedMessage<JsonNode>> TextMessageReader => _receiveTextChannel.Reader;

    public WebSocketConnectionState ConnectionState => (WebSocketConnectionState)Volatile.Read(ref _connectionStateValue);

    public static JsonSerializerOptions JsonWebOptions => JsonSerializerOptions.Web;

    public WebSocketClient(
        IEventDispatcher eventDispatcher,
        IOptions<WebSocketClientOptions> options,
        ILogger<WebSocketClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        _receiveBinaryChannel = Channel.CreateBounded(
            new BoundedChannelOptions(_options.ReceiveBinaryChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            },
            void (WebSocketReceivedMessage<ReadOnlyMemory<byte>> dropped) => _logger.ReceivedBinaryMessageDroppedWarning(_options.ServerUrl, dropped.ReceivedAt));
        _receiveTextChannel = Channel.CreateBounded(
            new BoundedChannelOptions(_options.ReceiveTextChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            },
            void (WebSocketReceivedMessage<JsonNode> dropped) => _logger.ReceivedTextMessageDroppedWarning(_options.ServerUrl, dropped.ReceivedAt, dropped.Data.ToJsonString(JsonWebOptions)));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        if (!disposing)
        {
            return;
        }

        _checkHealthCts?.Cancel();
        _receiveMessageCts?.Cancel();
        _disposalCts.Cancel();

        _checkHealthCts?.Dispose();
        _checkHealthCts = null;

        _receiveMessageCts?.Dispose();
        _receiveMessageCts = null;

        _ = _receiveBinaryChannel.Writer.TryComplete();
        _ = _receiveTextChannel.Writer.TryComplete();

        // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
        _checkHealthTask = null;
        _receiveMessageTask = null;

        _clientWebSocket?.Dispose();
        _clientWebSocket = null;

        _disposalCts.Dispose();

        _sendSemaphore.Dispose();
    }

    public virtual async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await ConnectAsync(null, cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task ConnectAsync(Func<string>? getWebSocketServerUrl = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        if (!TryConnectionStateTransition(WebSocketConnectionState.Disconnected, WebSocketConnectionState.Connecting))
        {
            _logger.ConnectNotDisconnectedStateError(_options.ServerUrl, ConnectionState);
            throw new InvalidOperationException("WebSocket is not disconnected");
        }

        try
        {
            await _eventDispatcher.DispatchEventAsync(new OnWebSocketConnecting(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.ConnectDispatchConnectingEventCanceledInfo(_options.ServerUrl, ConnectionState);
        }
        catch (Exception ex)
        {
            _logger.ConnectDispatchConnectingEventException(_options.ServerUrl, ConnectionState, ex);
        }

        _getWebSocketServerUrl = getWebSocketServerUrl;

        try
        {
            await ConnectInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.ConnectInternalCanceledInfo(_options.ServerUrl, ConnectionState);


            if (!TryConnectionStateTransition(WebSocketConnectionState.Connecting, WebSocketConnectionState.Disconnected))
            {
                _logger.ConnectInternalCanceledNotConnectingStateWarning(_options.ServerUrl, ConnectionState);
                throw;
            }

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnected("connect canceled"), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ConnectInternalCanceledDispatchDisconnectedEventCanceledInfo(_options.ServerUrl, ConnectionState);
            }
            catch (Exception exception)
            {
                _logger.ConnectInternalCanceledDispatchDisconnectedEventException(_options.ServerUrl, ConnectionState, exception);
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.ConnectInternalException(_options.ServerUrl, ConnectionState, ex);

            if (!TryConnectionStateTransition(WebSocketConnectionState.Connecting, WebSocketConnectionState.Disconnected))
            {
                _logger.ConnectInternalExceptionNotConnectingStateWarning(_options.ServerUrl, ConnectionState);
                throw;
            }

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnected($"connect error: {ex.Message}"), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ConnectInternalExceptionDispatchDisconnectedEventCanceledInfo(_options.ServerUrl, ConnectionState);
            }
            catch (Exception exception)
            {
                _logger.ConnectInternalExceptionDispatchDisconnectedEventException(_options.ServerUrl, ConnectionState, exception);
            }

            throw;
        }
    }

    public virtual async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        await DisconnectInternalAsync(Interlocked.Read(ref _connectionId), WebSocketCloseStatus.NormalClosure, "User initiated", true, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResponse?> SendJsonRpc2RequestAsync<TParams, TResponse>(JsonRpc2RequestDto<TParams> request, CancellationToken cancellationToken = default)
        where TResponse : class
    {
        return await SendRequestAsync<JsonRpc2RequestDto<TParams>, TResponse>(request.Id, request, cancellationToken);
    }

    public async Task SendNotificationAsync<TMessage>(TMessage messageObject, CancellationToken cancellationToken = default)
    {
        await SendTextMessageAsync(messageObject, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResponse?> SendRequestAsync<TRequest, TResponse>(string requestId, TRequest request, CancellationToken cancellationToken = default)
        where TResponse : class
    {
        string responseJson = await SendRequestAndWaitForResponse(requestId, request, cancellationToken).ConfigureAwait(false);

        try
        {
            TResponse? response = JsonSerializer.Deserialize<TResponse>(responseJson, JsonWebOptions);
            if (response == null)
            {
                _logger.SendRequestDeserializeResponseNullError(_options.ServerUrl, requestId, responseJson);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.SendRequestDeserializeResponseException(_options.ServerUrl, requestId, responseJson, ex);
            throw;
        }
    }

    protected virtual async Task AttemptReconnectAsync(CancellationToken cancellationToken = default)
    {
        WebSocketClientOptions.ReconnectionPolicy? policy = _options.Reconnection;
        if (policy == null)
        {
            _logger.AttemptReconnectNoReconnectionSettingsInfo(_options.ServerUrl);
            return;
        }

        if (!TryConnectionStateTransition(WebSocketConnectionState.Disconnected, WebSocketConnectionState.Reconnecting))
        {
            _logger.AttemptReconnectNotDisconnectedStateWarning(_options.ServerUrl, ConnectionState);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnReconnectError("Reconnect not disconnected web socket"), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.AttemptReconnectNotDisconnectedDispatchCanceledInfo(_options.ServerUrl);
            }
            catch (Exception ex)
            {
                _logger.AttemptReconnectNotDisconnectedDispatchException(_options.ServerUrl, ex);
            }

            return;
        }

        while (_isDisposed == 0 && (policy.MaxAttempts < 0 || _reconnectAttempts < policy.MaxAttempts))
        {
            int reconnectAttempts = Interlocked.Increment(ref _reconnectAttempts);
            _logger.AttemptReconnectDelayAttemptsInfo(_options.ServerUrl, policy.DelayMilliseconds, reconnectAttempts, policy.MaxAttempts);

            try
            {
                await Task.Delay(policy.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                await ConnectInternalAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                _logger.AttemptReconnectConnectCanceledInfo(_options.ServerUrl, reconnectAttempts, policy.MaxAttempts);

                if (!TryConnectionStateTransition(WebSocketConnectionState.Reconnecting, WebSocketConnectionState.Disconnected))
                {
                    _logger.AttemptReconnectConnectCanceledNotReconnectingStateWarning(_options.ServerUrl, ConnectionState, reconnectAttempts, policy.MaxAttempts);
                    throw;
                }

                try
                {
                    await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnected("reconnecting canceled"), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.AttemptReconnectConnectCanceledDispatchDisconnectedEventCanceledInfo(_options.ServerUrl, reconnectAttempts, policy.MaxAttempts);
                }
                catch (Exception exception)
                {
                    _logger.AttemptReconnectConnectCanceledDispatchDisconnectedEventException(_options.ServerUrl, reconnectAttempts, policy.MaxAttempts, exception);
                }

                throw;
            }
            catch (Exception ex)
            {
                _logger.AttemptReconnectConnectException(_options.ServerUrl, reconnectAttempts, policy.MaxAttempts, ex);
            }
        }

        _logger.AttemptReconnectMaxReconnectAttemptsReachedError(_options.ServerUrl, _reconnectAttempts, policy.MaxAttempts);

        if (!TryConnectionStateTransition(WebSocketConnectionState.Reconnecting, WebSocketConnectionState.Disconnected))
        {
            _logger.AttemptReconnectMaxReconnectAttemptsReachedNotReconnectingStateWarning(_options.ServerUrl, ConnectionState, _reconnectAttempts, policy.MaxAttempts);
            return;
        }

        try
        {
            await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnected("max reconnection attempts reached"), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.AttemptReconnectMaxReconnectAttemptsReachedDispatchDisconnectedEventCanceledInfo(_options.ServerUrl, _reconnectAttempts, policy.MaxAttempts);
        }
        catch (Exception exception)
        {
            _logger.AttemptReconnectMaxReconnectAttemptsReachedDispatchDisconnectedEventException(_options.ServerUrl, _reconnectAttempts, policy.MaxAttempts, exception);
        }

        try
        {
            await _eventDispatcher.DispatchEventAsync(new OnMaxReconnectionAttemptsReached(_reconnectAttempts), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.AttemptReconnectMaxReconnectAttemptsReachedDispatchEventCanceledInfo(_options.ServerUrl, _reconnectAttempts, policy.MaxAttempts);
        }
        catch (Exception ex)
        {
            _logger.AttemptReconnectMaxReconnectAttemptsReachedDispatchEventException(_options.ServerUrl, _reconnectAttempts, policy.MaxAttempts, ex);
        }
    }

    protected virtual async Task CheckHealthAsync(long connectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_options.HealthCheck!.IntervalMilliseconds), cancellationToken).ConfigureAwait(false);

                long lastReceivedTimestampMilliseconds = Interlocked.Read(ref _lastReceivedTimestampMilliseconds);
                if (lastReceivedTimestampMilliseconds + _options.HealthCheck.TimeoutMilliseconds < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                {
                    _logger.CheckHealthNoServerHeartbeatWarning(
                        _options.ServerUrl,
                        connectionId,
                        DateTimeOffset.FromUnixTimeMilliseconds(lastReceivedTimestampMilliseconds).ToString("u")
                    );

                    try
                    {
                        await _eventDispatcher.DispatchEventAsync(new OnNoServerHeartbeatReceived(lastReceivedTimestampMilliseconds), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.CheckHealthNoServerHeartbeatDispatchEventCanceledInfo(_options.ServerUrl, connectionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.CheckHealthNoServerHeartbeatDispatchEventException(_options.ServerUrl, connectionId, ex);
                    }

                    try
                    {
                        await DisconnectInternalAsync(connectionId, WebSocketCloseStatus.EndpointUnavailable, "No server heartbeat", false, true, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.CheckHealthDisconnectCanceledInfo(_options.ServerUrl, connectionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.CheckHealthDisconnectException(_options.ServerUrl, connectionId, ex);
                    }

                    using CancellationTokenSource reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);
                    try
                    {
                        await AttemptReconnectAsync(reconnectCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.CheckHealthAttemptReconnectCanceledInfo(_options.ServerUrl, connectionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.CheckHealthAttemptReconnectException(_options.ServerUrl, connectionId, ex);
                    }

                    return;
                }
            }

            _logger.CheckHealthCancelRequestedInfo(_options.ServerUrl, connectionId);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnHealthCheckCanceled(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.CheckHealthCancelRequestedDispatchEventCanceledInfo(_options.ServerUrl, connectionId);
            }
            catch (Exception ex)
            {
                _logger.CheckHealthCancelRequestedDispatchEventException(_options.ServerUrl, connectionId, ex);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.CheckHealthCanceledExceptionInfo(_options.ServerUrl, connectionId);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnHealthCheckCanceled(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.CheckHealthCanceledExceptionDispatchEventCanceledInfo(_options.ServerUrl, connectionId);
            }
            catch (Exception ex)
            {
                _logger.CheckHealthCanceledExceptionDispatchEventException(_options.ServerUrl, connectionId, ex);
            }
        }
        catch (Exception ex)
        {
            _logger.CheckHealthException(_options.ServerUrl, connectionId, ex);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnHealthCheckFailure(ex), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.CheckHealthExceptionDispatchEventCanceledInfo(_options.ServerUrl, connectionId);
            }
            catch (Exception exception)
            {
                _logger.CheckHealthExceptionDispatchEventException(_options.ServerUrl, connectionId, exception);
            }
        }
    }

    protected virtual async Task ConnectInternalAsync(CancellationToken cancellationToken = default)
    {
        _checkHealthCts?.Cancel();
        _receiveMessageCts?.Cancel();

        _checkHealthCts?.Dispose();
        _checkHealthCts = null;

        _receiveMessageCts?.Dispose();
        _receiveMessageCts = null;

        _clientWebSocket?.Dispose();
        _clientWebSocket = new ClientWebSocket();
        if (_options.KeepAliveIntervalMilliseconds > 0)
        {
            _clientWebSocket.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(_options.KeepAliveIntervalMilliseconds);
        }
        if (_options.KeepAliveTimeoutMilliseconds > 0)
        {
            _clientWebSocket.Options.KeepAliveTimeout = TimeSpan.FromMilliseconds(_options.KeepAliveTimeoutMilliseconds);
        }

        using CancellationTokenSource connectionTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ConnectionTimeoutMilliseconds > 0)
        {
            connectionTimeoutCts.CancelAfter(_options.ConnectionTimeoutMilliseconds);
        }

        int maxAttempts = _options.Reconnection?.MaxAttempts ?? 1;
        _logger.ConnectInternalConnectingToWebSocketInfo(_options.ServerUrl, _reconnectAttempts, maxAttempts);

        DrainReceiveChannels();

        long connectionId;
        try
        {
            string webSocketServerUrl = _getWebSocketServerUrl == null ? _options.ServerUrl : _getWebSocketServerUrl();
            await _clientWebSocket.ConnectAsync(new Uri(webSocketServerUrl), connectionTimeoutCts.Token).ConfigureAwait(false);

            _logger.ConnectInternalConnectedToWebSocketInfo(_options.ServerUrl, _reconnectAttempts, maxAttempts);

            lock (_connectionLock)
            {
                if (!TryConnectionStateTransition(WebSocketConnectionState.Connecting, WebSocketConnectionState.Connected) &&
                    !TryConnectionStateTransition(WebSocketConnectionState.Reconnecting, WebSocketConnectionState.Connected))
                {
                    _logger.ConnectInternalInvalidStateTransitionError(_options.ServerUrl, ConnectionState, _reconnectAttempts, maxAttempts);
                    throw new InvalidOperationException($"Cannot transition to Connected from {ConnectionState}");
                }

                _ = Interlocked.Exchange(ref _reconnectAttempts, 0);
                connectionId = Interlocked.Increment(ref _connectionId);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.ConnectInternalConnectCanceledInfo(_options.ServerUrl, _reconnectAttempts, maxAttempts);
            throw;
        }
        catch (Exception ex)
        {
            _logger.ConnectInternalConnectException(_options.ServerUrl, _reconnectAttempts, maxAttempts, ex);
            throw;
        }

        if (_options.HealthCheck != null)
        {
            _checkHealthCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);

            // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
            _ = Interlocked.Exchange(ref _lastReceivedTimestampMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _checkHealthTask = CheckHealthAsync(connectionId, _checkHealthCts.Token).ContinueWith(
                t => _logger.ConnectInternalCheckHealthTaskException(_options.ServerUrl, connectionId, t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        _receiveMessageCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);

        // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
        _receiveMessageTask = ReceiveMessagesAsync(connectionId, _receiveMessageCts.Token).ContinueWith(
            t => _logger.ConnectInternalReceiveMessageTaskException(_options.ServerUrl, connectionId, t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

        if (ConnectionState == WebSocketConnectionState.Connected && Interlocked.Read(ref _connectionId) == connectionId)
        {
            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnWebSocketConnected(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ConnectInternalDispatchConnectedEventCanceledInfo(_options.ServerUrl, connectionId, _reconnectAttempts, maxAttempts);
            }
            catch (Exception exception)
            {
                _logger.ConnectInternalDispatchConnectedEventException(_options.ServerUrl, connectionId, _reconnectAttempts, maxAttempts, exception);
            }
        }
    }

    protected virtual async Task DisconnectInternalAsync(long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription, bool shutdownCheckHealthTask, bool shutdownReceiveMessageTask, CancellationToken cancellationToken = default)
    {
        _logger.DisconnectInternalInfo(_options.ServerUrl, connectionId, closeStatus, statusDescription);

        lock (_connectionLock)
        {
            long currentConnectionId = Interlocked.Read(ref _connectionId);
            if (currentConnectionId != connectionId)
            {
                _logger.DisconnectInternalStaleConnectionInfo(_options.ServerUrl, connectionId, currentConnectionId, closeStatus, statusDescription);
                return;
            }

            if (!TryConnectionStateTransition(WebSocketConnectionState.Connected, WebSocketConnectionState.Disconnecting))
            {
                _logger.DisconnectInternalNotConnectedStateError(_options.ServerUrl, ConnectionState, connectionId, closeStatus, statusDescription);
                throw new InvalidOperationException("WebSocket is not connected");
            }
        }

        try
        {
            await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnecting(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.DisconnectInternalDispatchDisconnectingEventCanceledInfo(_options.ServerUrl, connectionId, closeStatus, statusDescription);
        }
        catch (Exception ex)
        {
            _logger.DisconnectInternalDispatchDisconnectingEventException(_options.ServerUrl, connectionId, closeStatus, statusDescription, ex);
        }

        try
        {
            if (shutdownCheckHealthTask)
            {
                await ShutdownCheckHealthTaskAsync(cancellationToken).ConfigureAwait(false);
            }

            if (shutdownReceiveMessageTask)
            {
                await ShutdownReceiveMessageTaskAsync(cancellationToken).ConfigureAwait(false);
            }

            PendingRequestCleanup(new WebSocketException("Disconnect requested"));

            ClientWebSocket? clientWebSocket = _clientWebSocket;
            if (clientWebSocket?.State == WebSocketState.Open)
            {
                using CancellationTokenSource disconnectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_options.GracefulShutdownTimeoutMilliseconds > 0)
                {
                    disconnectTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_options.GracefulShutdownTimeoutMilliseconds));
                }

                _logger.DisconnectInternalDisconnectingFromWebSocketInfo(_options.ServerUrl, connectionId, closeStatus, statusDescription);
                await clientWebSocket.CloseAsync(closeStatus, statusDescription, disconnectTimeoutCts.Token).ConfigureAwait(false);
                _logger.DisconnectInternalDisconnectedFromWebSocketInfo(_options.ServerUrl, connectionId, closeStatus, statusDescription);
            }
            else
            {
                _logger.DisconnectInternalUnconnectedWebSocketWarning(_options.ServerUrl, connectionId, closeStatus, statusDescription);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.DisconnectInternalCanceledInfo(_options.ServerUrl, connectionId, closeStatus, statusDescription);
            throw;
        }
        catch (Exception ex)
        {
            _logger.DisconnectInternalException(_options.ServerUrl, connectionId, closeStatus, statusDescription, ex);
            throw;
        }
        finally
        {
            _clientWebSocket?.Dispose();
            _clientWebSocket = null;
            if (!TryConnectionStateTransition(WebSocketConnectionState.Disconnecting, WebSocketConnectionState.Disconnected))
            {
                _logger.DisconnectInternalFinallyNotDisconnectingStateWarning(_options.ServerUrl, ConnectionState, connectionId, closeStatus, statusDescription);
            }
            else
            {
                try
                {
                    await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnected(statusDescription ?? string.Empty), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.DisconnectInternalFinallyDispatchDisconnectedEventCanceledInfo(_options.ServerUrl, connectionId, closeStatus, statusDescription);
                }
                catch (Exception ex)
                {
                    _logger.DisconnectInternalFinallyDispatchDisconnectedEventException(_options.ServerUrl, connectionId, closeStatus, statusDescription, ex);
                }
            }
        }
    }

    protected virtual void DrainReceiveChannels()
    {
        int totalTextDrained = 0;
        while (_receiveTextChannel.Reader.TryRead(out _))
        {
            totalTextDrained++;
        }

        int totalBinaryDrained = 0;
        while (_receiveBinaryChannel.Reader.TryRead(out _))
        {
            totalBinaryDrained++;
        }

        _logger.DrainReceiveChannelsInfo(_options.ServerUrl, totalTextDrained, totalBinaryDrained);
    }

    protected virtual string? GetRequestIdFromResponse(JsonNode jsonNode)
    {
        return (string?)jsonNode["id"];
    }

    protected virtual async Task HandleDisconnectionAsync(long connectionId, string? statusDescription, CancellationToken cancellationToken = default)
    {
        _logger.HandleDisconnectionInfo(_options.ServerUrl, connectionId, statusDescription);

        if (_isDisposed == 1)
        {
            _logger.HandleDisconnectionDisposedInfo(_options.ServerUrl, connectionId, statusDescription);
            return;
        }

        lock (_connectionLock)
        {
            long currentConnectionId = Interlocked.Read(ref _connectionId);
            if (currentConnectionId != connectionId)
            {
                _logger.HandleDisconnectionStaleConnectionInfo(_options.ServerUrl, connectionId, currentConnectionId, statusDescription);
                return;
            }

            if (!TryConnectionStateTransition(WebSocketConnectionState.Connected, WebSocketConnectionState.Disconnecting))
            {
                _logger.HandleDisconnectionNotConnectedStateInfo(_options.ServerUrl, ConnectionState, connectionId, statusDescription);
                return;
            }
        }

        try
        {
            await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnecting(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.HandleDisconnectionDispatchDisconnectingEventCanceledInfo(_options.ServerUrl, connectionId, statusDescription);
        }
        catch (Exception ex)
        {
            _logger.HandleDisconnectionDispatchDisconnectingEventException(_options.ServerUrl, connectionId, statusDescription, ex);
        }

        try
        {
            await ShutdownCheckHealthTaskAsync(cancellationToken).ConfigureAwait(false);

            PendingRequestCleanup(new WebSocketException("Connection lost"));
        }
        catch (OperationCanceledException)
        {
            _logger.HandleDisconnectionCanceledInfo(_options.ServerUrl, connectionId, statusDescription);
        }
        catch (Exception ex)
        {
            _logger.HandleDisconnectionException(_options.ServerUrl, connectionId, statusDescription, ex);
        }
        finally
        {
            _clientWebSocket?.Dispose();
            _clientWebSocket = null;
            if (!TryConnectionStateTransition(WebSocketConnectionState.Disconnecting, WebSocketConnectionState.Disconnected))
            {
                _logger.HandleDisconnectionFinallyNotDisconnectingStateWarning(_options.ServerUrl, ConnectionState, connectionId, statusDescription);
            }
            else
            {
                try
                {
                    await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnected(statusDescription ?? string.Empty), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.HandleDisconnectionFinallyDispatchDisconnectedEventCanceledInfo(_options.ServerUrl, connectionId, statusDescription);
                }
                catch (Exception ex)
                {
                    _logger.HandleDisconnectionFinallyDispatchDisconnectedEventException(_options.ServerUrl, connectionId, statusDescription, ex);
                }
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using CancellationTokenSource reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);
                await AttemptReconnectAsync(reconnectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.HandleDisconnectionAttemptReconnectCanceledInfo(_options.ServerUrl, connectionId, statusDescription);
            }
            catch (Exception ex)
            {
                _logger.HandleDisconnectionAttemptReconnectException(_options.ServerUrl, connectionId, statusDescription, ex);
            }
        }
    }

    protected virtual void PendingRequestCleanup(Exception exception)
    {
        foreach (KeyValuePair<object, TaskCompletionSource<string>> kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out TaskCompletionSource<string>? tcs))
            {
                _ = tcs.TrySetException(exception);
            }
        }
    }

    protected virtual void ProcessResponse(string requestId, string textResponse)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            _logger.ProcessResponseRequestIdMissingError(_options.ServerUrl, textResponse);
            return;
        }

        bool isFound = _pendingRequests.TryRemove(requestId, out TaskCompletionSource<string>? tcs);
        if (!isFound)
        {
            _logger.ProcessResponsePendingRequestNotFoundWarning(_options.ServerUrl, requestId, textResponse);
            return;
        }

        if (tcs == null)
        {
            _logger.ProcessResponseTaskCompletionSourceNullWarning(_options.ServerUrl, requestId, textResponse);
            return;
        }

        bool isSuccessful = tcs.TrySetResult(textResponse);
        if (!isSuccessful)
        {
            _logger.ProcessResponseSetResultError(_options.ServerUrl, requestId, textResponse);
        }
    }

    protected virtual async Task ProcessReceivedMessageAsync(WebSocketMessageType messageType, byte[] fullMessage, CancellationToken cancellationToken = default)
    {
        _logger.ProcessReceivedMessageDebug(_options.ServerUrl, fullMessage.Length);

        try
        {
            if (messageType == WebSocketMessageType.Text)
            {
                string textMessage = Encoding.UTF8.GetString(fullMessage);
                if (string.IsNullOrWhiteSpace(textMessage))
                {
                    _logger.ProcessReceivedMessageNullOrWhitespaceMessageError(_options.ServerUrl);
                    return;
                }

                JsonNode? messageJson;
                try
                {
                    messageJson = JsonNode.Parse(textMessage);
                }
                catch (Exception ex)
                {
                    _logger.ProcessReceivedMessageParseException(_options.ServerUrl, textMessage, ex);
                    return;
                }

                if (messageJson == null)
                {
                    _logger.ProcessReceivedMessageNullJsonError(_options.ServerUrl, textMessage);
                    return;
                }

                string? requestId = GetRequestIdFromResponse(messageJson);
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    try
                    {
                        ProcessResponse(requestId, textMessage);
                    }
                    catch (Exception ex)
                    {
                        _logger.ProcessReceivedMessageProcessResponseException(_options.ServerUrl, requestId, textMessage, ex);

                        try
                        {
                            await _eventDispatcher.DispatchEventAsync(new OnProcessResponseFailure(ex), cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.ProcessReceivedMessageDispatchProcessResponseFailureEventCanceledInfo(_options.ServerUrl, requestId, textMessage);
                        }
                        catch (Exception exception)
                        {
                            _logger.ProcessReceivedMessageDispatchProcessResponseFailureEventException(_options.ServerUrl, requestId, textMessage, exception);
                        }
                    }

                    return;
                }

                WebSocketReceivedMessage<JsonNode> channelMessage = new()
                {
                    Data = messageJson.DeepClone(),
                    ReceivedAt = DateTimeOffset.UtcNow
                };

                try
                {
                    await _receiveTextChannel.Writer.WriteAsync(channelMessage, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.ProcessReceivedMessageWriteReceiveTextChannelCanceledInfo(_options.ServerUrl);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.ProcessReceivedMessageWriteReceiveTextChannelException(_options.ServerUrl, textMessage, ex);

                    try
                    {
                        await _eventDispatcher.DispatchEventAsync(new OnWriteReceiveTextChannelFailure(ex), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.ProcessReceivedMessageDispatchWriteTextChannelFailureEventCanceledInfo(_options.ServerUrl, textMessage);
                    }
                    catch (Exception exception)
                    {
                        _logger.ProcessReceivedMessageDispatchWriteTextChannelFailureEventException(_options.ServerUrl, textMessage, exception);
                    }
                }
            }
            else if (messageType == WebSocketMessageType.Binary)
            {
                WebSocketReceivedMessage<ReadOnlyMemory<byte>> channelMessage = new()
                {
                    Data = fullMessage,
                    ReceivedAt = DateTimeOffset.UtcNow
                };

                try
                {
                    await _receiveBinaryChannel.Writer.WriteAsync(channelMessage, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.ProcessReceivedMessageWriteReceiveBinaryChannelCanceledInfo(_options.ServerUrl);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.ProcessReceivedMessageWriteReceiveBinaryChannelException(_options.ServerUrl, ex);

                    try
                    {
                        await _eventDispatcher.DispatchEventAsync(new OnWriteReceiveBinaryChannelFailure(ex), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.ProcessReceivedMessageDispatchWriteBinaryChannelFailureEventCanceledInfo(_options.ServerUrl);
                    }
                    catch (Exception exception)
                    {
                        _logger.ProcessReceivedMessageDispatchWriteBinaryChannelFailureEventException(_options.ServerUrl, exception);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.ProcessReceiveMessageException(_options.ServerUrl, messageType.ToString(), ex);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnProcessReceivedMessageFailure(ex), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ProcessReceivedMessageDispatchProcessReceiveMessageFailureEventCanceledInfo(_options.ServerUrl, messageType.ToString());
            }
            catch (Exception exception)
            {
                _logger.ProcessReceivedMessageDispatchProcessReceiveMessageFailureEventException(_options.ServerUrl, messageType.ToString(), exception);
            }
        }
    }

    protected virtual async Task ReceiveMessagesAsync(long connectionId, CancellationToken cancellationToken = default)
    {
        ClientWebSocket? clientWebSocket = _clientWebSocket;
        if (clientWebSocket == null)
        {
            _logger.ReceiveMessagesNullClientWebSocketError(_options.ServerUrl, connectionId);
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.ReceiveBufferByteSize);
        await using MemoryStream messageStream = new();

        try
        {
            bool exceededMaxAllowedMessageSize = false;
            while (!cancellationToken.IsCancellationRequested && clientWebSocket.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult result = await clientWebSocket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.ReceiveMessagesCloseMessageReceivedWarning(_options.ServerUrl, connectionId);

                    try
                    {
                        await clientWebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client Acknowledgment", cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.ReceiveMessagesCloseOutputException(_options.ServerUrl, connectionId, ex);
                    }

                    await HandleDisconnectionAsync(connectionId, "Close message received", cancellationToken).ConfigureAwait(false);

                    return;
                }

                _ = Interlocked.Exchange(ref _lastReceivedTimestampMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                if (exceededMaxAllowedMessageSize)
                {
                }
                else if (_options.MaxMessageByteSize > 0 && messageStream.Length + result.Count > _options.MaxMessageByteSize)
                {
                    exceededMaxAllowedMessageSize = true;
                    _logger.ReceiveMessagesExceedMaxAllowedSizeWarning(_options.ServerUrl, connectionId, _options.MaxMessageByteSize, messageStream.Length);
                }
                else
                {
                    messageStream.Write(buffer, 0, result.Count);
                }

                if (result.EndOfMessage)
                {
                    if (!exceededMaxAllowedMessageSize)
                    {
                        byte[] fullMessage = messageStream.ToArray();
                        await ProcessReceivedMessageAsync(result.MessageType, fullMessage, cancellationToken).ConfigureAwait(false);
                    }

                    messageStream.SetLength(0);
                    exceededMaxAllowedMessageSize = false;
                }
            }

            if (clientWebSocket.State == WebSocketState.Open)
            {
                _logger.ReceiveMessagesCancelRequestedInfo(_options.ServerUrl, connectionId);
            }
            else
            {
                _logger.ReceiveMessagesWebSocketNotConnectedInfo(_options.ServerUrl, connectionId);
                await HandleDisconnectionAsync(connectionId, "WebSocket not connected", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.ReceiveMessagesCanceledExceptionInfo(_options.ServerUrl, connectionId);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnReceiveCanceled(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ReceiveMessagesDispatchReceiveCanceledEventCanceledInfo(_options.ServerUrl, connectionId);
            }
            catch (Exception exception)
            {
                _logger.ReceiveMessagesDispatchReceiveCanceledEventException(_options.ServerUrl, connectionId, exception);
            }
        }
        catch (Exception ex)
        {
            _logger.ReceiveMessagesException(_options.ServerUrl, connectionId, ex);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnReceiveFailure(ex), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ReceiveMessagesDispatchReceiveFailureEventCanceledInfo(_options.ServerUrl, connectionId);
            }
            catch (Exception exception)
            {
                _logger.ReceiveMessagesDispatchReceiveFailureEventException(_options.ServerUrl, connectionId, exception);
            }

            await HandleDisconnectionAsync(connectionId, "Exception while receiving", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected virtual async Task<string> SendRequestAndWaitForResponse<TRequest>(string requestId, TRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        _logger.SendRequestAndWaitForResponseInfo(_options.ServerUrl, requestId);

        using CancellationTokenSource responseTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TaskCompletionSource<string> responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool isAdded = false;
        try
        {
            isAdded = _pendingRequests.TryAdd(requestId, responseTcs);
        }
        catch (Exception ex)
        {
            _logger.SendRequestAndWaitForResponseAddPendingRequestException(_options.ServerUrl, requestId, ex);
        }

        if (!isAdded)
        {
            _logger.SendRequestAndWaitForResponsePendingRequestNotAddedError(_options.ServerUrl, requestId);
            throw new ArgumentException("Duplicate request id", nameof(request));
        }

        try
        {
            await SendTextMessageAsync(request, cancellationToken).ConfigureAwait(false);

            if (_options.ResponseTimeoutMilliseconds > 0)
            {
                responseTimeoutCts.CancelAfter(_options.ResponseTimeoutMilliseconds);
            }

            return await responseTcs.Task.WaitAsync(responseTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.SendRequestAndWaitForResponseCanceledInfo(_options.ServerUrl, requestId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.SendRequestAndWaitForResponseException(_options.ServerUrl, requestId, ex);
            throw;
        }
        finally
        {
            _ = _pendingRequests.TryRemove(requestId, out _);
        }
    }

    protected virtual async Task SendTextMessageAsync<TMessage>(TMessage messageObject, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        if (!IsConnectionConnected)
        {
            _logger.SendTextMessageNotConnectedStateInfo(_options.ServerUrl);
            throw new InvalidOperationException("not connected state");
        }

        string messageJson;
        byte[] messageBytes;

        try
        {
            messageJson = JsonSerializer.Serialize(messageObject, JsonWebOptions);
            messageBytes = Encoding.UTF8.GetBytes(messageJson);
        }
        catch (Exception ex)
        {
            _logger.SendTextMessageSerializeException(_options.ServerUrl, ex);
            throw;
        }

        _logger.SendTextMessageDebug(_options.ServerUrl, messageJson);

        try
        {
            await _sendSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.SendTextMessageSemaphoreWaitCanceledInfo(_options.ServerUrl, messageJson);
            throw;
        }
        catch (Exception ex)
        {
            _logger.SendTextMessageSemaphoreWaitException(_options.ServerUrl, messageJson, ex);
            throw;
        }

        try
        {
            using CancellationTokenSource requestTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.RequestTimeoutMilliseconds > 0)
            {
                requestTimeoutCts.CancelAfter(_options.RequestTimeoutMilliseconds);
            }

            ClientWebSocket? clientWebSocket = _clientWebSocket;
            if (clientWebSocket == null)
            {
                _logger.SendTextMessageNullClientWebSocketWarning(_options.ServerUrl, messageJson);
                throw new InvalidOperationException("null client web socket");
            }
            if (clientWebSocket.State != WebSocketState.Open)
            {
                _logger.SendTextMessageClientWebSocketNotConnectedWarning(_options.ServerUrl, messageJson);
                throw new InvalidOperationException("client web socket not connected");
            }
            await clientWebSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, requestTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.SendTextMessageCanceledInfo(_options.ServerUrl, messageJson);
            throw;
        }
        catch (Exception ex)
        {
            _logger.SendTextMessageException(_options.ServerUrl, messageJson, ex);
            throw;
        }
        finally
        {
            _ = _sendSemaphore.Release();
        }
    }

    protected virtual async Task ShutdownCheckHealthTaskAsync(CancellationToken cancellationToken = default)
    {
        _logger.ShutdownCheckHealthTaskInfo(_options.ServerUrl);

        try
        {
            _checkHealthCts?.Cancel();

            if (_checkHealthTask != null)
            {
                await _checkHealthTask.WaitAsync(TimeSpan.FromMilliseconds(_options.GracefulShutdownTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (TimeoutException)
        {
            _logger.ShutdownCheckHealthTaskTimeoutExceptionInfo(_options.ServerUrl);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnShutdownCheckHealthTaskTimeout(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ShutdownCheckHealthTaskDispatchShutdownCheckHealthTaskTimeoutEventCanceledInfo(_options.ServerUrl);
            }
            catch (Exception exception)
            {
                _logger.ShutdownCheckHealthTaskDispatchShutdownCheckHealthTaskTimeoutEventException(_options.ServerUrl, exception);
            }
        }
        catch (Exception ex)
        {
            _logger.ShutdownCheckHealthTaskException(_options.ServerUrl, ex);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnShutdownCheckHealthTaskFailure(ex), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ShutdownCheckHealthTaskDispatchShutdownCheckHealthTaskFailureEventCanceledInfo(_options.ServerUrl);
            }
            catch (Exception exception)
            {
                _logger.ShutdownCheckHealthTaskDispatchShutdownCheckHealthTaskFailureEventException(_options.ServerUrl, exception);
            }
        }
        finally
        {
            // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
            _checkHealthTask = null;

            _checkHealthCts?.Dispose();
            _checkHealthCts = null;

        }
    }

    protected virtual async Task ShutdownReceiveMessageTaskAsync(CancellationToken cancellationToken = default)
    {
        _logger.ShutdownReceiveMessageTaskInfo(_options.ServerUrl);

        try
        {
            _receiveMessageCts?.Cancel();

            if (_receiveMessageTask != null)
            {
                await _receiveMessageTask.WaitAsync(TimeSpan.FromMilliseconds(_options.GracefulShutdownTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (TimeoutException)
        {
            _logger.ShutdownReceiveMessageTaskTimeoutExceptionInfo(_options.ServerUrl);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnShutdownReceiveMessageTaskTimeout(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ShutdownReceiveMessageTaskDispatchShutdownReceiveMessageTaskTimeoutEventCanceledInfo(_options.ServerUrl);
            }
            catch (Exception exception)
            {
                _logger.ShutdownReceiveMessageTaskDispatchShutdownReceiveMessageTaskTimeoutEventException(_options.ServerUrl, exception);
            }
        }
        catch (Exception ex)
        {
            _logger.ShutdownReceiveMessageTaskException(_options.ServerUrl, ex);

            try
            {
                await _eventDispatcher.DispatchEventAsync(new OnShutdownReceiveMessageTaskFailure(ex), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.ShutdownReceiveMessageTaskDispatchShutdownReceiveMessageTaskFailureEventCanceledInfo(_options.ServerUrl);
            }
            catch (Exception exception)
            {
                _logger.ShutdownReceiveMessageTaskDispatchShutdownReceiveMessageTaskFailureEventException(_options.ServerUrl, exception);
            }
        }
        finally
        {
            // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
            _receiveMessageTask = null;

            _receiveMessageCts?.Dispose();
            _receiveMessageCts = null;
        }
    }

    private bool TryConnectionStateTransition(WebSocketConnectionState fromState, WebSocketConnectionState toState)
    {
        if (!ValidConnectionStateTransitions.Contains((fromState, toState)))
        {
            _logger.TryTransitionConnectionStateInvalidError(_options.ServerUrl, fromState, toState);
            return false;
        }

        int originalState = Interlocked.CompareExchange(ref _connectionStateValue, (int)toState, (int)fromState);
        bool isSuccess = originalState == (int)fromState;
        if (!isSuccess)
        {
            _logger.TryTransitionConnectionStateRejectedWarning(_options.ServerUrl, fromState, toState, (WebSocketConnectionState)originalState);
        }

        return isSuccess;
    }
}

public static partial class WebSocketClientLogs
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Received binary message dropped at WebSocket {SocketUrl}: {ReceivedAt}")]
    public static partial void ReceivedBinaryMessageDroppedWarning(
        this ILogger logger, string socketUrl, DateTimeOffset receivedAt);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Warning,
        Message = "Received text message dropped at WebSocket {SocketUrl}: {ReceivedAt} {ReceivedData}")]
    public static partial void ReceivedTextMessageDroppedWarning(
        this ILogger logger, string socketUrl, DateTimeOffset receivedAt, string receivedData);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "ConnectAsync: not disconnected state at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectNotDisconnectedStateError(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Information,
        Message = "ConnectAsync: dispatch connecting event canceled at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectDispatchConnectingEventCanceledInfo(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Error,
        Message = "ConnectAsync: dispatch connecting event exception at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectDispatchConnectingEventException(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, Exception exception);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Information,
        Message = "ConnectAsync: internal canceled at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectInternalCanceledInfo(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState);

    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Warning,
        Message = "ConnectAsync: internal canceled not connecting state at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectInternalCanceledNotConnectingStateWarning(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState);

    [LoggerMessage(
        EventId = 1060,
        Level = LogLevel.Information,
        Message = "ConnectAsync: internal canceled dispatch disconnected event canceled at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectInternalCanceledDispatchDisconnectedEventCanceledInfo(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState);

    [LoggerMessage(
        EventId = 1070,
        Level = LogLevel.Information,
        Message = "ConnectAsync: internal canceled dispatch disconnected event exception at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectInternalCanceledDispatchDisconnectedEventException(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, Exception exception);

    [LoggerMessage(
        EventId = 1080,
        Level = LogLevel.Information,
        Message = "ConnectAsync: internal exception at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectInternalException(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, Exception exception);

    [LoggerMessage(
        EventId = 1090,
        Level = LogLevel.Warning,
        Message = "ConnectAsync: internal exception not connecting state at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectInternalExceptionNotConnectingStateWarning(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState);

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "ConnectAsync: dispatch disconnected event canceled at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectInternalExceptionDispatchDisconnectedEventCanceledInfo(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState);

    [LoggerMessage(
        EventId = 1110,
        Level = LogLevel.Error,
        Message = "ConnectAsync: dispatch disconnected event exception at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void ConnectInternalExceptionDispatchDisconnectedEventException(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, Exception exception);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Error,
        Message = "SendRequestAsync: deserialize response null at WebSocket {SocketUrl} with request id `{RequestId}` and response `{Response}`")]
    public static partial void SendRequestDeserializeResponseNullError(
        this ILogger logger, string socketUrl, string requestId, string response);

    [LoggerMessage(
        EventId = 2020,
        Level = LogLevel.Error,
        Message = "SendRequestAsync: deserialize response exception at WebSocket {SocketUrl} with request id `{RequestId}` and response `{Response}`")]
    public static partial void SendRequestDeserializeResponseException(
        this ILogger logger, string socketUrl, string requestId, string response, Exception exception);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Information,
        Message = "AttemptReconnectAsync: no reconnection settings at WebSocket {SocketUrl}")]
    public static partial void AttemptReconnectNoReconnectionSettingsInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Warning,
        Message = "AttemptReconnectAsync: not disconnected state at WebSocket {SocketUrl} with connection state `{ConnectionState}`")]
    public static partial void AttemptReconnectNotDisconnectedStateWarning(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState);

    [LoggerMessage(
        EventId = 3030,
        Level = LogLevel.Information,
        Message = "AttemptReconnectAsync: not disconnected dispatch canceled at WebSocket {SocketUrl}")]
    public static partial void AttemptReconnectNotDisconnectedDispatchCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 3040,
        Level = LogLevel.Error,
        Message = "AttemptReconnectAsync: not disconnected dispatch exception at WebSocket {SocketUrl}")]
    public static partial void AttemptReconnectNotDisconnectedDispatchException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 3050,
        Level = LogLevel.Information,
        Message = "AttemptReconnectAsync: reconnecting WebSocket at {SocketUrl} in {DelayMilliseconds} ms on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectDelayAttemptsInfo(
        this ILogger logger, string socketUrl, int delayMilliseconds, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3060,
        Level = LogLevel.Information,
        Message = "AttemptReconnectAsync: connect canceled at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectConnectCanceledInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3070,
        Level = LogLevel.Warning,
        Message = "AttemptReconnectAsync: connect canceled not reconnecting state at WebSocket {SocketUrl} with connection state `{ConnectionState}` on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectConnectCanceledNotReconnectingStateWarning(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3080,
        Level = LogLevel.Information,
        Message = "AttemptReconnectAsync: connect canceled dispatch disconnected event canceled at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectConnectCanceledDispatchDisconnectedEventCanceledInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3090,
        Level = LogLevel.Error,
        Message = "AttemptReconnectAsync: connect canceled dispatch disconnected event exception at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectConnectCanceledDispatchDisconnectedEventException(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts, Exception exception);

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Information,
        Message = "AttemptReconnectAsync: connect canceled dispatch event canceled at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectConnectCanceledDispatchEventCanceledInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3110,
        Level = LogLevel.Error,
        Message = "AttemptReconnectAsync: connect canceled dispatch event exception at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectConnectCanceledDispatchEventException(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts, Exception exception);

    [LoggerMessage(
        EventId = 3120,
        Level = LogLevel.Error,
        Message = "AttemptReconnectAsync: connect exception at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectConnectException(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts, Exception exception);

    [LoggerMessage(
        EventId = 3130,
        Level = LogLevel.Information,
        Message = "AttemptReconnectAsync: connect exception dispatch event canceled at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectConnectExceptionDispatchEventCanceledInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3140,
        Level = LogLevel.Error,
        Message = "AttemptReconnectAsync: connect exception dispatch event exception at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectConnectExceptionDispatchEventException(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts, Exception exception);

    [LoggerMessage(
        EventId = 3150,
        Level = LogLevel.Error,
        Message = "AttemptReconnectAsync: max reconnect attempts reached at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectMaxReconnectAttemptsReachedError(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3160,
        Level = LogLevel.Warning,
        Message = "AttemptReconnectAsync: max reconnect attempts reached not reconnecting state at WebSocket {SocketUrl} with connection state `{ConnectionState}` on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectMaxReconnectAttemptsReachedNotReconnectingStateWarning(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3170,
        Level = LogLevel.Information,
        Message = "AttemptReconnectAsync: max reconnect attempts reached dispatch disconnected event canceled at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectMaxReconnectAttemptsReachedDispatchDisconnectedEventCanceledInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3180,
        Level = LogLevel.Error,
        Message = "AttemptReconnectAsync: max reconnect attempts reached dispatch disconnected event exception at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectMaxReconnectAttemptsReachedDispatchDisconnectedEventException(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts, Exception exception);

    [LoggerMessage(
        EventId = 3190,
        Level = LogLevel.Information,
        Message = "AttemptReconnectAsync: max reconnect attempts reached dispatch event canceled at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectMaxReconnectAttemptsReachedDispatchEventCanceledInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Error,
        Message = "AttemptReconnectAsync: max reconnect attempts reached dispatch event exception at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void AttemptReconnectMaxReconnectAttemptsReachedDispatchEventException(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts, Exception exception);

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Warning,
        Message = "CheckHealthAsync: no server heartbeat received at WebSocket {SocketUrl} for connection id {ConnectionId} since {LastReceivedUtcDateTime}")]
    public static partial void CheckHealthNoServerHeartbeatWarning(
        this ILogger logger, string socketUrl, long connectionId, string lastReceivedUtcDateTime);

    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Information,
        Message = "CheckHealthAsync: no server heartbeat dispatch event canceled at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthNoServerHeartbeatDispatchEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 4030,
        Level = LogLevel.Error,
        Message = "CheckHealthAsync: no server heartbeat dispatch event exception at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthNoServerHeartbeatDispatchEventException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 4040,
        Level = LogLevel.Information,
        Message = "CheckHealthAsync: disconnect canceled at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthDisconnectCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 4050,
        Level = LogLevel.Error,
        Message = "CheckHealthAsync: disconnect exception at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthDisconnectException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 4060,
        Level = LogLevel.Information,
        Message = "CheckHealthAsync: attempt reconnect canceled at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthAttemptReconnectCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 4070,
        Level = LogLevel.Error,
        Message = "CheckHealthAsync: attempt reconnect exception at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthAttemptReconnectException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 4080,
        Level = LogLevel.Information,
        Message = "CheckHealthAsync: cancel requested at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthCancelRequestedInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 4090,
        Level = LogLevel.Information,
        Message = "CheckHealthAsync: cancel requested dispatch event canceled at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthCancelRequestedDispatchEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Error,
        Message = "CheckHealthAsync: cancel requested dispatch event exception at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthCancelRequestedDispatchEventException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 4110,
        Level = LogLevel.Information,
        Message = "CheckHealthAsync: canceled exception at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthCanceledExceptionInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 4120,
        Level = LogLevel.Information,
        Message = "CheckHealthAsync: canceled exception dispatch event canceled at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthCanceledExceptionDispatchEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 4130,
        Level = LogLevel.Error,
        Message = "CheckHealthAsync: canceled exception dispatch event exception at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthCanceledExceptionDispatchEventException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 4140,
        Level = LogLevel.Error,
        Message = "CheckHealthAsync: exception at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 4150,
        Level = LogLevel.Information,
        Message = "CheckHealthAsync: exception dispatch event canceled at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthExceptionDispatchEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 4160,
        Level = LogLevel.Error,
        Message = "CheckHealthAsync: exception dispatch event exception at WebSocket {SocketUrl} for connection id {ConnectionId}")]
    public static partial void CheckHealthExceptionDispatchEventException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Information,
        Message = "ConnectInternalAsync: Connecting to WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void ConnectInternalConnectingToWebSocketInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 5020,
        Level = LogLevel.Information,
        Message = "ConnectInternalAsync: Connected to WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void ConnectInternalConnectedToWebSocketInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 5030,
        Level = LogLevel.Error,
        Message = "ConnectInternalAsync: invalid state transition at WebSocket {SocketUrl} with connection state `{ConnectionState}` on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void ConnectInternalInvalidStateTransitionError(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 5040,
        Level = LogLevel.Information,
        Message = "ConnectInternalAsync: Connect canceled at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void ConnectInternalConnectCanceledInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 5050,
        Level = LogLevel.Error,
        Message = "ConnectInternalAsync: connect exception at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void ConnectInternalConnectException(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts, Exception exception);

    [LoggerMessage(
        EventId = 5060,
        Level = LogLevel.Error,
        Message = "ConnectInternalAsync: check health task exception at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ConnectInternalCheckHealthTaskException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 5070,
        Level = LogLevel.Error,
        Message = "ConnectInternalAsync: receive message task exception at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ConnectInternalReceiveMessageTaskException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 5080,
        Level = LogLevel.Information,
        Message = "ConnectInternalAsync: dispatch connected event canceled at WebSocket {SocketUrl} with connection id {ConnectionId} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void ConnectInternalDispatchConnectedEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 5090,
        Level = LogLevel.Error,
        Message = "ConnectInternalAsync: dispatch connected event canceled at WebSocket {SocketUrl} with connection id {ConnectionId} on attempt {ReconnectAttempts} of {MaxAttempts}")]
    public static partial void ConnectInternalDispatchConnectedEventException(
        this ILogger logger, string socketUrl, long connectionId, int reconnectAttempts, int maxAttempts, Exception exception);

    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Information,
        Message = "DisconnectInternalAsync: disconnect requested at WebSocket {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalInfo(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6020,
        Level = LogLevel.Information,
        Message = "DisconnectInternalAsync: stale connection at WebSocket {SocketUrl} for connection id {ConnectionId} (current {CurrentConnectionId}), close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalStaleConnectionInfo(
        this ILogger logger, string socketUrl, long connectionId, long currentConnectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6030,
        Level = LogLevel.Error,
        Message = "DisconnectInternalAsync: not connected state at {SocketUrl} with connection state `{ConnectionState}` for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalNotConnectedStateError(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6040,
        Level = LogLevel.Information,
        Message = "DisconnectInternalAsync: dispatch disconnecting event canceled at {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalDispatchDisconnectingEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6050,
        Level = LogLevel.Error,
        Message = "DisconnectInternalAsync: dispatch disconnecting event exception at {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalDispatchDisconnectingEventException(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription, Exception exception);

    [LoggerMessage(
        EventId = 6060,
        Level = LogLevel.Information,
        Message = "DisconnectInternalAsync: disconnecting from WebSocket at {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalDisconnectingFromWebSocketInfo(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6070,
        Level = LogLevel.Information,
        Message = "DisconnectInternalAsync: disconnected WebSocket at {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalDisconnectedFromWebSocketInfo(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6080,
        Level = LogLevel.Warning,
        Message = "DisconnectInternalAsync: unconnected WebSocket at {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalUnconnectedWebSocketWarning(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6090,
        Level = LogLevel.Information,
        Message = "DisconnectInternalAsync: disconnect canceled at WebSocket {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Error,
        Message = "DisconnectInternalAsync: disconnect exception at WebSocket {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalException(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription, Exception exception);

    [LoggerMessage(
        EventId = 6110,
        Level = LogLevel.Warning,
        Message = "DisconnectInternalAsync: disconnect finally not disconnecting state at WebSocket {SocketUrl} with connection state `{ConnectionState}` for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalFinallyNotDisconnectingStateWarning(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6120,
        Level = LogLevel.Information,
        Message = "DisconnectInternalAsync: disconnect finally dispatch disconnected event canceled at WebSocket {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalFinallyDispatchDisconnectedEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription);

    [LoggerMessage(
        EventId = 6130,
        Level = LogLevel.Error,
        Message = "DisconnectInternalAsync: disconnect finally dispatch disconnected event exception at WebSocket {SocketUrl} for connection id {ConnectionId}, close status {CloseStatus} and status description `{StatusDescription}`")]
    public static partial void DisconnectInternalFinallyDispatchDisconnectedEventException(
        this ILogger logger, string socketUrl, long connectionId, WebSocketCloseStatus closeStatus, string? statusDescription, Exception exception);

    [LoggerMessage(
        EventId = 7010,
        Level = LogLevel.Warning,
        Message = "DrainReceiveChannelsInfo: WebSocket {SocketUrl} with total text and binary receive channels drained of {TotalTexTDrained} and {TotalBinaryDrain} respectively")]
    public static partial void DrainReceiveChannelsInfo(
        this ILogger logger, string socketUrl, int totalTexTDrained, int totalBinaryDrain);

    [LoggerMessage(
        EventId = 8010,
        Level = LogLevel.Information,
        Message = "HandleDisconnectionAsync: WebSocket {SocketUrl} for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionInfo(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription);

    [LoggerMessage(
        EventId = 8020,
        Level = LogLevel.Information,
        Message = "HandleDisconnectionAsync: disposed at WebSocket {SocketUrl} for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionDisposedInfo(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription);

    [LoggerMessage(
        EventId = 8030,
        Level = LogLevel.Information,
        Message = "HandleDisconnectionAsync: stale connection at WebSocket {SocketUrl} for connection id {ConnectionId} (current {CurrentConnectionId}) with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionStaleConnectionInfo(
        this ILogger logger, string socketUrl, long connectionId, long currentConnectionId, string? statusDescription);

    [LoggerMessage(
        EventId = 8040,
        Level = LogLevel.Information,
        Message = "HandleDisconnectionAsync: not connected state at WebSocket {SocketUrl} with connection state `{ConnectionState}` for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionNotConnectedStateInfo(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, long connectionId, string? statusDescription);

    [LoggerMessage(
        EventId = 8050,
        Level = LogLevel.Information,
        Message = "HandleDisconnectionAsync: dispatch disconnecting event canceled at WebSocket {SocketUrl}  for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionDispatchDisconnectingEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription);

    [LoggerMessage(
        EventId = 8060,
        Level = LogLevel.Error,
        Message = "HandleDisconnectionAsync: dispatch disconnecting event exception at WebSocket {SocketUrl} for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionDispatchDisconnectingEventException(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription, Exception exception);

    [LoggerMessage(
        EventId = 8070,
        Level = LogLevel.Information,
        Message = "HandleDisconnectionAsync: canceled at WebSocket {SocketUrl} for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription);

    [LoggerMessage(
        EventId = 8080,
        Level = LogLevel.Error,
        Message = "HandleDisconnectionAsync: exception at WebSocket {SocketUrl} for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionException(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription, Exception exception);

    [LoggerMessage(
        EventId = 8090,
        Level = LogLevel.Warning,
        Message = "HandleDisconnectionAsync: finally not disconnecting state at WebSocket {SocketUrl} with connection state `{ConnectionState}` for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionFinallyNotDisconnectingStateWarning(
        this ILogger logger, string socketUrl, WebSocketConnectionState connectionState, long connectionId, string? statusDescription);

    [LoggerMessage(
        EventId = 8100,
        Level = LogLevel.Information,
        Message = "HandleDisconnectionAsync: finally dispatch disconnected event canceled at WebSocket {SocketUrl} for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionFinallyDispatchDisconnectedEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription);

    [LoggerMessage(
        EventId = 8110,
        Level = LogLevel.Error,
        Message = "HandleDisconnectionAsync: finally dispatch disconnected event exception at WebSocket {SocketUrl} for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionFinallyDispatchDisconnectedEventException(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription, Exception exception);

    [LoggerMessage(
        EventId = 8120,
        Level = LogLevel.Information,
        Message = "HandleDisconnectionAsync: attempt reconnect canceled at WebSocket {SocketUrl} for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionAttemptReconnectCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription);

    [LoggerMessage(
        EventId = 8130,
        Level = LogLevel.Error,
        Message = "HandleDisconnectionAsync: attempt reconnect exception at WebSocket {SocketUrl} for connection id {ConnectionId} with status description `{StatusDescription}`")]
    public static partial void HandleDisconnectionAttemptReconnectException(
        this ILogger logger, string socketUrl, long connectionId, string? statusDescription, Exception exception);

    [LoggerMessage(
        EventId = 9010,
        Level = LogLevel.Error,
        Message = "ProcessResponse: request ID missing at WebSocket {SocketUrl} for response `{TextResponse}`")]
    public static partial void ProcessResponseRequestIdMissingError(
        this ILogger logger, string socketUrl, string textResponse);

    [LoggerMessage(
        EventId = 9020,
        Level = LogLevel.Warning,
        Message = "ProcessResponse: pending request not found at WebSocket {SocketUrl} with request id {RequestId}) for response `{TextResponse}`")]
    public static partial void ProcessResponsePendingRequestNotFoundWarning(
        this ILogger logger, string socketUrl, string requestId, string textResponse);

    [LoggerMessage(
        EventId = 9030,
        Level = LogLevel.Warning,
        Message = "ProcessResponse: task completion source null at WebSocket {SocketUrl} with request id {RequestId}) for response `{TextResponse}`")]
    public static partial void ProcessResponseTaskCompletionSourceNullWarning(
        this ILogger logger, string socketUrl, string requestId, string textResponse);

    [LoggerMessage(
        EventId = 9040,
        Level = LogLevel.Error,
        Message = "ProcessResponse: set result error at WebSocket {SocketUrl} with request id {RequestId}) for response `{TextResponse}`")]
    public static partial void ProcessResponseSetResultError(
        this ILogger logger, string socketUrl, string requestId, string textResponse);

    [LoggerMessage(
        EventId = 10010,
        Level = LogLevel.Debug,
        Message = "ProcessReceivedMessage: message received at WebSocket {SocketUrl} with size of {MessageSize} bytes")]
    public static partial void ProcessReceivedMessageDebug(
        this ILogger logger, string socketUrl, long messageSize);

    [LoggerMessage(
        EventId = 10020,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: null or whitespace message received at WebSocket {SocketUrl}")]
    public static partial void ProcessReceivedMessageNullOrWhitespaceMessageError(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 10030,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: parse exception at WebSocket {SocketUrl} for text message `{TextMessage}`")]
    public static partial void ProcessReceivedMessageParseException(
        this ILogger logger, string socketUrl, string textMessage, Exception exception);

    [LoggerMessage(
        EventId = 10040,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: null json at WebSocket {SocketUrl} for text message `{TextMessage}`")]
    public static partial void ProcessReceivedMessageNullJsonError(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 10050,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: process JSON-RPC 2.0 response exception at WebSocket {SocketUrl} for request id {RequestId} and text message `{TextMessage}`")]
    public static partial void ProcessReceivedMessageProcessResponseException(
        this ILogger logger, string socketUrl, string requestId, string textMessage, Exception exception);

    [LoggerMessage(
        EventId = 10060,
        Level = LogLevel.Information,
        Message = "ProcessReceivedMessage: dispatch process response failure event canceled at WebSocket {SocketUrl} for request id {RequestId} and text message `{TextMessage}`")]
    public static partial void ProcessReceivedMessageDispatchProcessResponseFailureEventCanceledInfo(
        this ILogger logger, string socketUrl, string requestId, string textMessage);

    [LoggerMessage(
        EventId = 10070,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: dispatch process response failure event exception at WebSocket {SocketUrl} for request id {RequestId} and text message `{TextMessage}`")]
    public static partial void ProcessReceivedMessageDispatchProcessResponseFailureEventException(
        this ILogger logger, string socketUrl, string requestId, string textMessage, Exception exception);

    [LoggerMessage(
        EventId = 10080,
        Level = LogLevel.Information,
        Message = "ProcessReceivedMessage: write receive text channel canceled at WebSocket {SocketUrl}")]
    public static partial void ProcessReceivedMessageWriteReceiveTextChannelCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 10090,
        Level = LogLevel.Information,
        Message = "ProcessReceivedMessage: dispatch write receive text channel failure event canceled at WebSocket {SocketUrl} for text message `{TextMessage}`")]
    public static partial void ProcessReceivedMessageDispatchWriteTextChannelFailureEventCanceledInfo(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 10100,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: dispatch write receive text channel failure event exception at WebSocket {SocketUrl} for text message `{TextMessage}`")]
    public static partial void ProcessReceivedMessageDispatchWriteTextChannelFailureEventException(
        this ILogger logger, string socketUrl, string textMessage, Exception exception);

    [LoggerMessage(
        EventId = 10110,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: write receive text channel exception at WebSocket {SocketUrl} for text message `{TextMessage})`")]
    public static partial void ProcessReceivedMessageWriteReceiveTextChannelException(
        this ILogger logger, string socketUrl, string textMessage, Exception exception);

    [LoggerMessage(
        EventId = 10120,
        Level = LogLevel.Information,
        Message = "ProcessReceivedMessage: write receive binary channel canceled at WebSocket {SocketUrl}")]
    public static partial void ProcessReceivedMessageWriteReceiveBinaryChannelCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 10130,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: write receive binary channel exception at WebSocket {SocketUrl}")]
    public static partial void ProcessReceivedMessageWriteReceiveBinaryChannelException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 10140,
        Level = LogLevel.Information,
        Message = "ProcessReceivedMessage: dispatch write binary channel failure canceled at WebSocket {SocketUrl}")]
    public static partial void ProcessReceivedMessageDispatchWriteBinaryChannelFailureEventCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 10150,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: dispatch write binary channel failure exception at WebSocket {SocketUrl}")]
    public static partial void ProcessReceivedMessageDispatchWriteBinaryChannelFailureEventException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 10160,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: process received message exception at WebSocket {SocketUrl} for message type `{MessageType}`")]
    public static partial void ProcessReceiveMessageException(
        this ILogger logger, string socketUrl, string messageType, Exception exception);

    [LoggerMessage(
        EventId = 10170,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: dispatch process received message failure event canceled at WebSocket {SocketUrl} for message type `{MessageType}`")]
    public static partial void ProcessReceivedMessageDispatchProcessReceiveMessageFailureEventCanceledInfo(
        this ILogger logger, string messageType, string socketUrl);

    [LoggerMessage(
        EventId = 10180,
        Level = LogLevel.Error,
        Message = "ProcessReceivedMessage: dispatch process received message failure event exception at WebSocket {SocketUrl} for message type `{MessageType}`")]
    public static partial void ProcessReceivedMessageDispatchProcessReceiveMessageFailureEventException(
        this ILogger logger, string messageType, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 11010,
        Level = LogLevel.Error,
        Message = "ReceiveMessages: null client websocket at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesNullClientWebSocketError(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 11020,
        Level = LogLevel.Warning,
        Message = "ReceiveMessages: close message at WebSocket {SocketUrl} with connection id {ConnectionID}")]
    public static partial void ReceiveMessagesCloseMessageReceivedWarning(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 11030,
        Level = LogLevel.Error,
        Message = "ReceiveMessages: close output exception at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesCloseOutputException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 11040,
        Level = LogLevel.Warning,
        Message = "ReceiveMessages: exceed max allowed size at WebSocket {SocketUrl} with connection id {ConnectionId}: {messageByteSize} bytes (max {maxAllowedByteSize} bytes)")]
    public static partial void ReceiveMessagesExceedMaxAllowedSizeWarning(
        this ILogger logger, string socketUrl, long connectionId, long maxAllowedByteSize, long messageByteSize);

    [LoggerMessage(
        EventId = 11050,
        Level = LogLevel.Information,
        Message = "ReceiveMessages: canceled at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesCancelRequestedInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 11060,
        Level = LogLevel.Information,
        Message = "ReceiveMessages: websocket not connected at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesWebSocketNotConnectedInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 11070,
        Level = LogLevel.Information,
        Message = "ReceiveMessages: message receiving canceled exception at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesCanceledExceptionInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 11080,
        Level = LogLevel.Information,
        Message = "ReceiveMessages: dispatch message receiving canceled event canceled at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesDispatchReceiveCanceledEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 11090,
        Level = LogLevel.Error,
        Message = "ReceiveMessages: dispatch message receiving canceled event exception at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesDispatchReceiveCanceledEventException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 11100,
        Level = LogLevel.Error,
        Message = "ReceiveMessages: exception at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 11110,
        Level = LogLevel.Error,
        Message = "ReceiveMessages: dispatch receive failure event canceled at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesDispatchReceiveFailureEventCanceledInfo(
        this ILogger logger, string socketUrl, long connectionId);

    [LoggerMessage(
        EventId = 11120,
        Level = LogLevel.Error,
        Message = "ReceiveMessages: dispatch receive failure event exception at WebSocket {SocketUrl} with connection id {ConnectionId}")]
    public static partial void ReceiveMessagesDispatchReceiveFailureEventException(
        this ILogger logger, string socketUrl, long connectionId, Exception exception);

    [LoggerMessage(
        EventId = 12010,
        Level = LogLevel.Information,
        Message = "SendRequestAndWaitForResponse: WebSocket {SocketUrl} for request id {RequestId})")]
    public static partial void SendRequestAndWaitForResponseInfo(
        this ILogger logger, string socketUrl, string requestId);

    [LoggerMessage(
        EventId = 12020,
        Level = LogLevel.Error,
        Message = "SendRequestAndWaitForResponse: add pending request exception at WebSocket {SocketUrl} for request id {RequestId})")]
    public static partial void SendRequestAndWaitForResponseAddPendingRequestException(
        this ILogger logger, string socketUrl, string requestId, Exception exception);

    [LoggerMessage(
        EventId = 12030,
        Level = LogLevel.Error,
        Message = "SendRequestAndWaitForResponse: pending request not added at WebSocket {SocketUrl} for request id {RequestId}")]
    public static partial void SendRequestAndWaitForResponsePendingRequestNotAddedError(
        this ILogger logger, string socketUrl, string requestId);

    [LoggerMessage(
        EventId = 12040,
        Level = LogLevel.Information,
        Message = "SendRequestAndWaitForResponse: canceled at WebSocket {SocketUrl} for request id {RequestId}")]
    public static partial void SendRequestAndWaitForResponseCanceledInfo(
        this ILogger logger, string socketUrl, string requestId);

    [LoggerMessage(
        EventId = 12050,
        Level = LogLevel.Error,
        Message = "SendRequestAndWaitForResponse: exception at WebSocket {SocketUrl} for request id {RequestId}")]
    public static partial void SendRequestAndWaitForResponseException(
        this ILogger logger, string socketUrl, string requestId, Exception exception);

    [LoggerMessage(
        EventId = 13010,
        Level = LogLevel.Information,
        Message = "SendTextMessage: not connected state at {SocketUrl}")]
    public static partial void SendTextMessageNotConnectedStateInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 13020,
        Level = LogLevel.Error,
        Message = "SendTextMessage: serialize exception at WebSocket {SocketUrl}")]
    public static partial void SendTextMessageSerializeException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 13030,
        Level = LogLevel.Debug,
        Message = "SendTextMessage: sending message at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void SendTextMessageDebug(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 13040,
        Level = LogLevel.Information,
        Message = "SendTextMessage: semaphore wait canceled at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void SendTextMessageSemaphoreWaitCanceledInfo(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 13050,
        Level = LogLevel.Error,
        Message = "SendTextMessage: semaphore wait exception at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void SendTextMessageSemaphoreWaitException(
        this ILogger logger, string socketUrl, string textMessage, Exception exception);

    [LoggerMessage(
        EventId = 13060,
        Level = LogLevel.Warning,
        Message = "SendTextMessage: null client websocket at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void SendTextMessageNullClientWebSocketWarning(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 13070,
        Level = LogLevel.Warning,
        Message = "SendTextMessage: client websocket not connected at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void SendTextMessageClientWebSocketNotConnectedWarning(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 13080,
        Level = LogLevel.Information,
        Message = "SendTextMessage: send canceled at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void SendTextMessageCanceledInfo(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 13090,
        Level = LogLevel.Error,
        Message = "SendTextMessage: send exception at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void SendTextMessageException(
        this ILogger logger, string socketUrl, string textMessage, Exception exception);

    [LoggerMessage(
        EventId = 14010,
        Level = LogLevel.Information,
        Message = "ShutdownCheckHealthTask: WebSocket {SocketUrl}")]
    public static partial void ShutdownCheckHealthTaskInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 14020,
        Level = LogLevel.Information,
        Message = "ShutdownCheckHealthTask: timeout at WebSocket {SocketUrl}")]
    public static partial void ShutdownCheckHealthTaskTimeoutExceptionInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 14030,
        Level = LogLevel.Information,
        Message = "ShutdownCheckHealthTask: dispatch shutdown check health task timeout event canceled at WebSocket {SocketUrl}")]
    public static partial void ShutdownCheckHealthTaskDispatchShutdownCheckHealthTaskTimeoutEventCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 14040,
        Level = LogLevel.Error,
        Message = "ShutdownCheckHealthTask: dispatch shutdown check health task timeout event exception at WebSocket {SocketUrl}")]
    public static partial void ShutdownCheckHealthTaskDispatchShutdownCheckHealthTaskTimeoutEventException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 14050,
        Level = LogLevel.Error,
        Message = "ShutdownCheckHealthTask: exception at WebSocket {SocketUrl}")]
    public static partial void ShutdownCheckHealthTaskException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 14060,
        Level = LogLevel.Information,
        Message = "ShutdownCheckHealthTask: dispatch shutdown check health task failure event canceled at WebSocket {SocketUrl}")]
    public static partial void ShutdownCheckHealthTaskDispatchShutdownCheckHealthTaskFailureEventCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 14070,
        Level = LogLevel.Error,
        Message = "ShutdownCheckHealthTask: dispatch shutdown check health task failure event exception at WebSocket {SocketUrl}")]
    public static partial void ShutdownCheckHealthTaskDispatchShutdownCheckHealthTaskFailureEventException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 15010,
        Level = LogLevel.Information,
        Message = "ShutdownReceiveMessageTask: WebSocket {SocketUrl}")]
    public static partial void ShutdownReceiveMessageTaskInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 15020,
        Level = LogLevel.Information,
        Message = "ShutdownReceiveMessageTask: timeout at WebSocket {SocketUrl}")]
    public static partial void ShutdownReceiveMessageTaskTimeoutExceptionInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 15030,
        Level = LogLevel.Information,
        Message = "ShutdownReceiveMessageTask: dispatch shutdown receive message task timeout event timeout at WebSocket {SocketUrl}")]
    public static partial void ShutdownReceiveMessageTaskDispatchShutdownReceiveMessageTaskTimeoutEventCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 15040,
        Level = LogLevel.Error,
        Message = "ShutdownReceiveMessageTask: dispatch shutdown receive message task timeout event timeout at WebSocket {SocketUrl}")]
    public static partial void ShutdownReceiveMessageTaskDispatchShutdownReceiveMessageTaskTimeoutEventException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 15050,
        Level = LogLevel.Error,
        Message = "ShutdownReceiveMessageTask: exception at WebSocket {SocketUrl}")]
    public static partial void ShutdownReceiveMessageTaskException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 15060,
        Level = LogLevel.Information,
        Message = "ShutdownReceiveMessageTask: dispatch shutdown receive message task failure event timeout at WebSocket {SocketUrl}")]
    public static partial void ShutdownReceiveMessageTaskDispatchShutdownReceiveMessageTaskFailureEventCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 15070,
        Level = LogLevel.Error,
        Message = "ShutdownReceiveMessageTask: dispatch shutdown receive message task failure event timeout at WebSocket {SocketUrl}")]
    public static partial void ShutdownReceiveMessageTaskDispatchShutdownReceiveMessageTaskFailureEventException(
        this ILogger logger, string socketUrl, Exception exception);

    [LoggerMessage(
        EventId = 16010,
        Level = LogLevel.Error,
        Message = "TryTransitionConnectionState: invalid state transition at WebSocket {SocketUrl} from `{FromState}` to `{ToState}")]
    public static partial void TryTransitionConnectionStateInvalidError(
        this ILogger logger, string socketUrl, WebSocketConnectionState fromState, WebSocketConnectionState toState);

    [LoggerMessage(
        EventId = 16020,
        Level = LogLevel.Warning,
        Message = "TryTransitionConnectionState: state transition rejected at WebSocket {SocketUrl} from `{FromState}` to `{ToState} for original state `{OriginalState}`")]
    public static partial void TryTransitionConnectionStateRejectedWarning(
        this ILogger logger, string socketUrl, WebSocketConnectionState fromState, WebSocketConnectionState toState, WebSocketConnectionState originalState);
}
