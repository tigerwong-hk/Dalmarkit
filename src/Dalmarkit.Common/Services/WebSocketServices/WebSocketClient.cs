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
    public const int PendingRequestsInitialCapacity = 8209;

    private readonly IEventDispatcher _eventDispatcher;
    private readonly ConcurrentDictionary<object, TaskCompletionSource<string>> _pendingRequests = new(Environment.ProcessorCount, PendingRequestsInitialCapacity);
    private readonly WebSocketClientOptions _options;
    private readonly ILogger<WebSocketClient> _logger;

    private readonly CancellationTokenSource _disposalCts = new();

    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

    private readonly Channel<WebSocketReceivedMessage<ReadOnlyMemory<byte>>> _receiveBinaryChannel;
    private readonly Channel<WebSocketReceivedMessage<string>> _receiveTextChannel;

    private CancellationTokenSource? _checkHealthCts;
    private CancellationTokenSource? _receiveMessageCts;

    private Task? _checkHealthTask;
    private Task? _receiveMessageTask;

    private ClientWebSocket? _clientWebSocket;

    private bool _isDisposed;
    private long _lastReceivedTimestampMilliseconds;
    private int _reconnectAttempts;

    private volatile WebSocketConnectionState _connectionState = WebSocketConnectionState.Disconnected;

    public bool IsConnected => _clientWebSocket?.State == WebSocketState.Open;
    public bool HasReachedMaxReconnectAttempts => _reconnectAttempts >= (_options.Reconnection?.MaxAttempts ?? 0);

    public ChannelReader<WebSocketReceivedMessage<ReadOnlyMemory<byte>>> BinaryMessageReader => _receiveBinaryChannel.Reader;
    public ChannelReader<WebSocketReceivedMessage<string>> TextMessageReader => _receiveTextChannel.Reader;

    public WebSocketConnectionState ConnectionState => _connectionState;

    public static readonly JsonSerializerOptions JsonWebOptions = new(JsonSerializerDefaults.Web);

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
            void (WebSocketReceivedMessage<string> dropped) => _logger.ReceivedTextMessageDroppedWarning(_options.ServerUrl, dropped.ReceivedAt, dropped.Data));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (!disposing)
        {
            return;
        }

        _checkHealthCts?.Dispose();
        _checkHealthCts = null;

        _receiveMessageCts?.Dispose();
        _receiveMessageCts = null;

        _ = _receiveBinaryChannel.Writer.TryComplete();
        _ = _receiveTextChannel.Writer.TryComplete();

        _checkHealthTask?.Dispose();
        _checkHealthTask = null;

        _receiveMessageTask?.Dispose();
        _receiveMessageTask = null;

        _clientWebSocket?.Dispose();
        _clientWebSocket = null;

        _disposalCts.Dispose();
        _connectionSemaphore.Dispose();
        _sendSemaphore.Dispose();
    }

    public virtual async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_connectionState != WebSocketConnectionState.Disconnected)
            {
                _logger.ConnectNotDisconnectedWebSocketInfo(_options.ServerUrl);
                throw new InvalidOperationException("WebSocket is not disconnected");
            }

            _connectionState = WebSocketConnectionState.Connecting;
        }
        catch (OperationCanceledException)
        {
            _logger.WaitToConnectCanceledInfo(_options.ServerUrl);
            throw;
        }
        catch (Exception ex)
        {
            _logger.WaitToConnectException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            _ = _connectionSemaphore.Release();
        }

        try
        {
            await ConnectInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _connectionState = WebSocketConnectionState.Disconnected;
            throw;
        }

    }

    public virtual async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await DisconnectInternalAsync(WebSocketCloseStatus.NormalClosure, "User initiated", true, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendJsonRpc2NotificationAsync<TMessage>(TMessage messageObject, CancellationToken cancellationToken = default)
    {
        await SendTextMessageAsync(messageObject, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResponse?> SendJsonRpc2RequestAsync<TParams, TResponse>(JsonRpc2RequestDto<TParams> request, CancellationToken cancellationToken = default)
        where TResponse : class
    {
        string responseJson = await SendAndWaitForJsonRpc2Response(request, cancellationToken).ConfigureAwait(false);

        try
        {
            TResponse? response = JsonSerializer.Deserialize<TResponse>(responseJson, JsonWebOptions);
            if (response == null)
            {
                _logger.JsonRpc2ResponseDeserializationError(_options.ServerUrl, request.Id, request.Method, responseJson);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.JsonRpc2ResponseDeserializationException(_options.ServerUrl, request.Id, request.Method, responseJson, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }
    }

    protected virtual async Task AttemptReconnectAsync(CancellationToken cancellationToken = default)
    {
        WebSocketClientOptions.ReconnectionPolicy? policy = _options.Reconnection;
        if (policy == null)
        {
            _logger.NoReconnectionSettingsInfo(_options.ServerUrl);
            return;
        }

        try
        {
            await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_connectionState != WebSocketConnectionState.Disconnected)
            {
                _logger.ReconnectNotDisconnectedWebSocketInfo(_options.ServerUrl);
                await _eventDispatcher.DispatchEventAsync(new OnReconnectError("Reconnect not disconnected web socket"), cancellationToken).ConfigureAwait(false);
                return;
            }

            _connectionState = WebSocketConnectionState.Reconnecting;
        }
        catch (OperationCanceledException)
        {
            _logger.ReconnectSemaphoreCanceledInfo(_options.ServerUrl);
            await _eventDispatcher.DispatchEventAsync(new OnReconnectCanceled(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.ReconnectSemaphoreException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            await _eventDispatcher.DispatchEventAsync(new OnReconnectFailure(_reconnectAttempts, ex), cancellationToken).ConfigureAwait(false);
            return;
        }
        finally
        {
            _ = _connectionSemaphore.Release();
        }

        while (!_isDisposed && (policy.MaxAttempts < 0 || _reconnectAttempts < policy.MaxAttempts))
        {
            _reconnectAttempts++;
            _logger.ReconnectingWebSocketDelayInfo(_options.ServerUrl, policy.DelayMilliseconds, _reconnectAttempts, policy.MaxAttempts);

            try
            {
                await Task.Delay(policy.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                await ConnectInternalAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                _connectionState = WebSocketConnectionState.Disconnected;
                _logger.ReconnectCanceledInfo(_options.ServerUrl);
                await _eventDispatcher.DispatchEventAsync(new OnReconnectCanceled(), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                _logger.ReconnectFailedException(_options.ServerUrl, _reconnectAttempts, policy.MaxAttempts, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                await _eventDispatcher.DispatchEventAsync(new OnReconnectFailure(_reconnectAttempts, ex), cancellationToken).ConfigureAwait(false);
            }
        }

        _connectionState = WebSocketConnectionState.Disconnected;
        _logger.MaxReconnectionAttemptsReachedError(_options.ServerUrl, _reconnectAttempts);
        await _eventDispatcher.DispatchEventAsync(new OnMaxReconnectionAttemptsReached(_reconnectAttempts), cancellationToken).ConfigureAwait(false);
    }

    protected virtual async Task CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_options.HealthCheck!.IntervalMilliseconds), cancellationToken).ConfigureAwait(false);

                if (_lastReceivedTimestampMilliseconds + _options.HealthCheck.TimeoutMilliseconds < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                {
                    _logger.NoServerHeartbeatWarning(
                        _options.ServerUrl,
                        DateTimeOffset.FromUnixTimeMilliseconds(_lastReceivedTimestampMilliseconds).ToString("u")
                    );

                    await _eventDispatcher.DispatchEventAsync(new OnNoServerHeartbeatReceived(_lastReceivedTimestampMilliseconds), cancellationToken).ConfigureAwait(false);

                    try
                    {
                        await DisconnectInternalAsync(WebSocketCloseStatus.EndpointUnavailable, "No server heartbeat", false, true, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await _eventDispatcher.DispatchEventAsync(new OnNoServerHeartbeatDisconnectFailure(ex), cancellationToken).ConfigureAwait(false);
                    }

                    await AttemptReconnectAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            _logger.HealthCheckCanceledInfo(_options.ServerUrl);
            await _eventDispatcher.DispatchEventAsync(new OnHealthCheckCanceled(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.HealthCheckCanceledExceptionInfo(_options.ServerUrl);
            await _eventDispatcher.DispatchEventAsync(new OnHealthCheckCanceled(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.HealthCheckUnexpectedException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            await _eventDispatcher.DispatchEventAsync(new OnHealthCheckFailure(ex), cancellationToken).ConfigureAwait(false);
        }
    }

    protected virtual async Task ConnectInternalAsync(CancellationToken cancellationToken = default)
    {
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
        _logger.ConnectingToWebSocketInfo(_options.ServerUrl, _reconnectAttempts, maxAttempts);

        try
        {
            await _clientWebSocket.ConnectAsync(new Uri(_options.ServerUrl), connectionTimeoutCts.Token).ConfigureAwait(false);

            _connectionState = WebSocketConnectionState.Connected;
            _logger.ConnectedToWebSocketInfo(_options.ServerUrl, _reconnectAttempts, maxAttempts);
            _reconnectAttempts = 0;
        }
        catch (OperationCanceledException)
        {
            _logger.ConnectCanceledInfo(_options.ServerUrl);
            throw;
        }
        catch (Exception ex)
        {
            _logger.ConnectFailedException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }

        if (_options.HealthCheck != null)
        {
            _checkHealthCts?.Dispose();
            _checkHealthCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);

            _checkHealthTask?.Dispose();
            _lastReceivedTimestampMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _checkHealthTask = CheckHealthAsync(_checkHealthCts.Token);
        }

        _receiveMessageCts?.Dispose();
        _receiveMessageCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);

        _receiveMessageTask?.Dispose();
        _receiveMessageTask = ReceiveMessagesAsync(_receiveMessageCts.Token);

        await _eventDispatcher.DispatchEventAsync(new OnWebSocketConnected(), cancellationToken).ConfigureAwait(false);
    }

    protected virtual async Task DisconnectInternalAsync(WebSocketCloseStatus closeStatus, string? statusDescription, bool shutdownCheckHealthTask, bool shutdownReceiveMessageTask, CancellationToken cancellationToken = default)
    {
        try
        {
            await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_connectionState != WebSocketConnectionState.Connected)
            {
                _logger.DisconnectNotConnectedWebSocketError(_options.ServerUrl);
                throw new InvalidOperationException("WebSocket is not connected");
            }

            _connectionState = WebSocketConnectionState.Disconnecting;
        }
        catch (OperationCanceledException)
        {
            _logger.DisconnectSemaphoreCanceledInfo(_options.ServerUrl);
            throw;
        }
        catch (Exception ex)
        {
            _logger.DisconnectSemaphoreException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            _ = _connectionSemaphore.Release();
        }

        if (shutdownCheckHealthTask)
        {
            await ShutdownCheckHealthTaskAsync(cancellationToken).ConfigureAwait(false);
        }

        if (shutdownReceiveMessageTask)
        {
            await ShutdownReceiveMessageTaskAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (IsConnected)
            {
                using CancellationTokenSource disconnectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_options.GracefulShutdownTimeoutMilliseconds > 0)
                {
                    disconnectTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_options.GracefulShutdownTimeoutMilliseconds));
                }

                _logger.DisconnectingFromWebSocketInfo(_options.ServerUrl);

                await _clientWebSocket!.CloseAsync(closeStatus, statusDescription, disconnectTimeoutCts.Token).ConfigureAwait(false);

                _logger.DisconnectedFromWebSocketInfo(_options.ServerUrl);
                await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnected(statusDescription ?? string.Empty), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.DisconnectUnconnectedWebSocketWarning(_options.ServerUrl);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.DisconnectCanceledInfo(_options.ServerUrl);
            throw;
        }
        catch (Exception ex)
        {
            _logger.DisconnectException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            _connectionState = WebSocketConnectionState.Disconnected;
            _clientWebSocket?.Dispose();
            _clientWebSocket = null;
        }
    }

    protected virtual async Task HandleDisconnectionAsync(string? statusDescription, CancellationToken cancellationToken = default)
    {
        try
        {
            await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_isDisposed)
            {
                _logger.HandleDisconnectWhenDisposedInfo(_options.ServerUrl);
                return;
            }

            if (_connectionState == WebSocketConnectionState.Disconnecting)
            {
                _logger.HandleDisconnectWhenDisconnectingInfo(_options.ServerUrl);
                return;
            }

            _connectionState = WebSocketConnectionState.Disconnected;
        }
        catch (OperationCanceledException)
        {
            _logger.HandleDisconnectSemaphoreCanceledInfo(_options.ServerUrl);
            throw;
        }
        finally
        {
            _ = _connectionSemaphore.Release();
        }

        await ShutdownCheckHealthTaskAsync(cancellationToken).ConfigureAwait(false);

        await _eventDispatcher.DispatchEventAsync(new OnWebSocketDisconnected(statusDescription ?? string.Empty), cancellationToken).ConfigureAwait(false);

        _clientWebSocket?.Dispose();
        _clientWebSocket = null;

        if (!cancellationToken.IsCancellationRequested)
        {
            await AttemptReconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected virtual void ProcessJsonRpc2Response(string requestId, string textResponse)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            _logger.JsonRpc2ResponseIdMissingError(_options.ServerUrl, textResponse);
            return;
        }

        bool isFound = _pendingRequests.TryRemove(requestId, out TaskCompletionSource<string>? tcs);
        if (!isFound)
        {
            _logger.PendingRequestNotFoundWarning(_options.ServerUrl, requestId, textResponse);
            return;
        }

        if (tcs == null)
        {
            _logger.PendingRequestNullWarning(_options.ServerUrl, requestId, textResponse);
            return;
        }

        bool isSuccessful = tcs.TrySetResult(textResponse);
        if (!isSuccessful)
        {
            _logger.JsonRpc2ResponseReturnError(_options.ServerUrl, requestId, textResponse);
        }
    }

    protected virtual async Task ProcessReceivedMessageAsync(WebSocketMessageType messageType, byte[] fullMessage, CancellationToken cancellationToken = default)
    {
        _logger.ReceivedMessageDebug(messageType.ToString(), _options.ServerUrl, fullMessage.Length);

        try
        {
            if (messageType == WebSocketMessageType.Text)
            {
                string textMessage = Encoding.UTF8.GetString(fullMessage);
                if (string.IsNullOrWhiteSpace(textMessage))
                {
                    _logger.NullOrWhitespaceMessageReceivedError(_options.ServerUrl);
                    return;
                }

                JsonNode? messageJson;
                try
                {
                    messageJson = JsonNode.Parse(textMessage);
                }
                catch (Exception ex)
                {
                    _logger.ParseTextMessageReceivedException(_options.ServerUrl, textMessage, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                    return;
                }

                if (messageJson == null)
                {
                    _logger.ParseTextMessageReceivedError(_options.ServerUrl, textMessage);
                    return;
                }

                string? jsonrpc = (string?)messageJson["jsonrpc"];
                if (!string.IsNullOrWhiteSpace(jsonrpc) && jsonrpc.Equals("2.0", StringComparison.Ordinal))
                {
                    string? id = (string?)messageJson["id"];
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        try
                        {
                            ProcessJsonRpc2Response(id, textMessage);
                        }
                        catch (Exception ex)
                        {
                            _logger.ProcessJsonRpc2ResponseException(_options.ServerUrl, id, textMessage, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                            await _eventDispatcher.DispatchEventAsync(new OnProcessJsonRpc2ResponseFailure(ex), cancellationToken).ConfigureAwait(false);
                        }

                        return;
                    }
                }

                WebSocketReceivedMessage<string> channelMessage = new()
                {
                    Data = textMessage,
                    ReceivedAt = DateTimeOffset.UtcNow
                };

                try
                {
                    await _receiveTextChannel.Writer.WriteAsync(channelMessage, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.WriteReceiveTextChannelCanceledInfo(_options.ServerUrl);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.WriteReceiveTextChannelException(_options.ServerUrl, textMessage, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                    await _eventDispatcher.DispatchEventAsync(new OnWriteReceiveTextChannelFailure(ex), cancellationToken).ConfigureAwait(false);
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
                    _logger.WriteReceiveBinaryChannelCanceledInfo(_options.ServerUrl);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.WriteReceiveBinaryChannelException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                    await _eventDispatcher.DispatchEventAsync(new OnWriteReceiveBinaryChannelFailure(ex), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.ProcessReceiveMessageException(_options.ServerUrl, messageType.ToString(), ex.Message, ex.InnerException?.Message, ex.StackTrace);
            await _eventDispatcher.DispatchEventAsync(new OnProcessReceivedMessageFailure(ex), cancellationToken).ConfigureAwait(false);
        }
    }

    protected virtual async Task ReceiveMessagesAsync(CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.ReceiveBufferByteSize);
        await using MemoryStream messageStream = new();

        try
        {
            bool exceededMaxAllowedMessageSize = false;
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                ValueWebSocketReceiveResult result = await _clientWebSocket!.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.ReceivedCloseMessageWarning(_options.ServerUrl);

                    await HandleDisconnectionAsync("Close message received", cancellationToken).ConfigureAwait(false);
                    return;
                }

                _lastReceivedTimestampMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (exceededMaxAllowedMessageSize)
                {
                }
                else if (_options.MaxMessageByteSize > 0 && messageStream.Length + result.Count > _options.MaxMessageByteSize)
                {
                    exceededMaxAllowedMessageSize = true;
                    _logger.ReceivedMessageExceedsMaxAllowedSizeWarning(_options.MaxMessageByteSize, _options.ServerUrl, messageStream.Length);
                }
                else
                {
                    messageStream.Write(buffer, 0, result.Count);
                }

                if (result.EndOfMessage)
                {
                    byte[] fullMessage = messageStream.ToArray();
                    messageStream.SetLength(0);
                    exceededMaxAllowedMessageSize = false;

                    await ProcessReceivedMessageAsync(result.MessageType, fullMessage, cancellationToken).ConfigureAwait(false);
                }
            }

            if (IsConnected)
            {
                _logger.MessagingReceivingCanceledInfo(_options.ServerUrl);
            }
            else
            {
                await HandleDisconnectionAsync("WebSocket not connected", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.MessagingReceivingCanceledExceptionInfo(_options.ServerUrl);
            await _eventDispatcher.DispatchEventAsync(new OnReceiveCanceled(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            _logger.ReceiveWebSocketException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            await _eventDispatcher.DispatchEventAsync(new OnReceiveFailure(ex), cancellationToken).ConfigureAwait(false);
            await HandleDisconnectionAsync("WebSocket exception while receiving", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.ReceiveUnexpectedException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            await _eventDispatcher.DispatchEventAsync(new OnReceiveFailure(ex), cancellationToken).ConfigureAwait(false);
            await HandleDisconnectionAsync("Unexpected exception while receiving", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected virtual async Task<string> SendAndWaitForJsonRpc2Response<TParams>(JsonRpc2RequestDto<TParams> request, CancellationToken cancellationToken = default)
    {
        await SendTextMessageAsync(request, cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource responseTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ResponseTimeoutMilliseconds > 0)
        {
            responseTimeoutCts.CancelAfter(_options.ResponseTimeoutMilliseconds);
        }

        TaskCompletionSource<string> responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool isAdded = false;
        try
        {
            isAdded = _pendingRequests.TryAdd(request.Id, responseTcs);
        }
        catch (Exception ex)
        {
            _logger.AddPendingRequestException(_options.ServerUrl, request.Id, request.Method, ex.Message, ex.InnerException?.Message, ex.StackTrace);
        }

        if (!isAdded)
        {
            _logger.AddPendingRequestError(_options.ServerUrl, request.Id, request.Method);
            throw new ArgumentException("Duplicate request id", nameof(request));
        }

        try
        {
            return await responseTcs.Task.WaitAsync(responseTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.WaitForJsonRpc2ResponseCanceledInfo(_options.ServerUrl, request.Id, request.Method);
            throw;
        }
        catch (Exception ex)
        {
            _logger.WaitForJsonRpc2ResponseException(_options.ServerUrl, request.Id, request.Method, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            _ = _pendingRequests.TryRemove(request.Id, out _);
        }
    }

    protected virtual async Task SendTextMessageAsync<TMessage>(TMessage messageObject, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!IsConnected)
        {
            _logger.SendTextMessageWebSocketNotConnectedInfo(_options.ServerUrl);
            throw new InvalidOperationException("WebSocket is not connected");
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
            _logger.SendTextMessageSerializationException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }

        _logger.SendingTextMessageDebug(_options.ServerUrl, messageJson);

        try
        {
            await _sendSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.WaitToSendTextMessageCanceledInfo(_options.ServerUrl, messageJson);
            throw;
        }
        catch (Exception ex)
        {
            _logger.WaitToSendTextMessageException(_options.ServerUrl, messageJson, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }

        using CancellationTokenSource requestTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.RequestTimeoutMilliseconds > 0)
        {
            requestTimeoutCts.CancelAfter(_options.RequestTimeoutMilliseconds);
        }

        try
        {
            await _clientWebSocket!.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, requestTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.SendTextMessageCanceledInfo(_options.ServerUrl, messageJson);
            throw;
        }
        catch (Exception ex)
        {
            _logger.SendTextMessageException(_options.ServerUrl, messageJson, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            _ = _sendSemaphore.Release();
        }
    }

    protected virtual async Task ShutdownCheckHealthTaskAsync(CancellationToken cancellationToken = default)
    {
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
            _logger.HealthCheckTaskShutdownTimeoutInfo(_options.ServerUrl);
            await _eventDispatcher.DispatchEventAsync(new OnShutdownCheckHealthTaskTimeout(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.HealthCheckTaskShutdownException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            await _eventDispatcher.DispatchEventAsync(new OnShutdownCheckHealthTaskFailure(ex), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _checkHealthCts?.Dispose();
            _checkHealthCts = null;

            _checkHealthTask?.Dispose();
            _checkHealthTask = null;
        }
    }

    protected virtual async Task ShutdownReceiveMessageTaskAsync(CancellationToken cancellationToken = default)
    {
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
            _logger.ReceiveMessageTaskShutdownTimeoutInfo(_options.ServerUrl);
            await _eventDispatcher.DispatchEventAsync(new OnShutdownReceiveMessageTaskTimeout(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.ReceiveMessageTaskShutdownException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            await _eventDispatcher.DispatchEventAsync(new OnShutdownReceiveMessageTaskFailure(ex), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _receiveMessageCts?.Dispose();
            _receiveMessageCts = null;

            _receiveMessageTask?.Dispose();
            _receiveMessageTask = null;
        }
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
        EventId = 30,
        Level = LogLevel.Information,
        Message = "Connect not disconnected at WebSocket {SocketUrl}")]
    public static partial void ConnectNotDisconnectedWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Information,
        Message = "Wait to connect canceled at WebSocket {SocketUrl}")]
    public static partial void WaitToConnectCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Error,
        Message = "Wait to connect exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void WaitToConnectException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Error,
        Message = "JSON-RPC 2.0 response deserialization error at WebSocket {SocketUrl} (RequestId: {RequestId} and Method: {Method}): {Response}")]
    public static partial void JsonRpc2ResponseDeserializationError(
        this ILogger logger, string socketUrl, string requestId, string method, string response);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Error,
        Message = "JSON-RPC 2.0 response deserialization exception at WebSocket {SocketUrl} (RequestId: {RequestId}, Method: {Method} and Response: {Response}) with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void JsonRpc2ResponseDeserializationException(
        this ILogger logger, string socketUrl, string requestId, string method, string response, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Information,
        Message = "Not reconnecting as no reconnection settings at WebSocket {SocketUrl}")]
    public static partial void NoReconnectionSettingsInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Information,
        Message = "Reconnect not disconnected at WebSocket {SocketUrl}")]
    public static partial void ReconnectNotDisconnectedWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Information,
        Message = "Reconnect semaphore canceled at WebSocket {SocketUrl}")]
    public static partial void ReconnectSemaphoreCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 110,
        Level = LogLevel.Error,
        Message = "Reconnect semaphore exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReconnectSemaphoreException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 120,
        Level = LogLevel.Information,
        Message = "Reconnecting WebSocket at {SocketUrl} in {DelayMilliseconds} ms (attempt {ReconnectAttempts} of {MaxAttempts})")]
    public static partial void ReconnectingWebSocketDelayInfo(
        this ILogger logger, string socketUrl, int delayMilliseconds, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 130,
        Level = LogLevel.Information,
        Message = "Reconnection canceled at WebSocket {SocketUrl}")]
    public static partial void ReconnectCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 140,
        Level = LogLevel.Error,
        Message = "Reconnection failed at WebSocket {SocketUrl} on attempt {ReconnectAttempts} of {MaxAttempts} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReconnectFailedException(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 150,
        Level = LogLevel.Error,
        Message = "Max reconnection attempts reached at WebSocket {SocketUrl}: {ReconnectAttempts}")]
    public static partial void MaxReconnectionAttemptsReachedError(
        this ILogger logger, string socketUrl, int reconnectAttempts);

    [LoggerMessage(
        EventId = 160,
        Level = LogLevel.Warning,
        Message = "No server heartbeat received at WebSocket {SocketUrl} since {LastReceivedUtcDateTime}")]
    public static partial void NoServerHeartbeatWarning(
        this ILogger logger, string socketUrl, string lastReceivedUtcDateTime);

    [LoggerMessage(
        EventId = 170,
        Level = LogLevel.Information,
        Message = "Health check canceled at WebSocket {SocketUrl}")]
    public static partial void HealthCheckCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 180,
        Level = LogLevel.Information,
        Message = "Health check canceled exception at WebSocket {SocketUrl}")]
    public static partial void HealthCheckCanceledExceptionInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 190,
        Level = LogLevel.Error,
        Message = "Health check unexpected exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void HealthCheckUnexpectedException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Information,
        Message = "Connecting to WebSocket at {SocketUrl} (attempt {ReconnectAttempts} of {MaxAttempts})")]
    public static partial void ConnectingToWebSocketInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 210,
        Level = LogLevel.Information,
        Message = "Connected to WebSocket at {SocketUrl} (attempt {ReconnectAttempts} of {MaxAttempts})")]
    public static partial void ConnectedToWebSocketInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 220,
        Level = LogLevel.Information,
        Message = "Connect canceled at WebSocket {SocketUrl}")]
    public static partial void ConnectCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 230,
        Level = LogLevel.Error,
        Message = "Connection failed at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ConnectFailedException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 240,
        Level = LogLevel.Error,
        Message = "Disconnect disconnecting or disconnected WebSocket at {SocketUrl}")]
    public static partial void DisconnectNotConnectedWebSocketError(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 250,
        Level = LogLevel.Information,
        Message = "Disconnect semaphore canceled at WebSocket {SocketUrl}")]
    public static partial void DisconnectSemaphoreCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 260,
        Level = LogLevel.Error,
        Message = "Disconnect semaphore exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void DisconnectSemaphoreException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 270,
        Level = LogLevel.Information,
        Message = "Disconnecting from WebSocket at {SocketUrl}")]
    public static partial void DisconnectingFromWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 280,
        Level = LogLevel.Information,
        Message = "Disconnected WebSocket at {SocketUrl}")]
    public static partial void DisconnectedFromWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 290,
        Level = LogLevel.Warning,
        Message = "Disconnect unconnected WebSocket at {SocketUrl}")]
    public static partial void DisconnectUnconnectedWebSocketWarning(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 300,
        Level = LogLevel.Information,
        Message = "Disconnect canceled at WebSocket {SocketUrl}")]
    public static partial void DisconnectCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 310,
        Level = LogLevel.Error,
        Message = "Disconnection failed at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void DisconnectException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 320,
        Level = LogLevel.Information,
        Message = "Handle disconnect when disposed at WebSocket {SocketUrl}")]
    public static partial void HandleDisconnectWhenDisposedInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 330,
        Level = LogLevel.Information,
        Message = "Handle disconnect when disconnecting at WebSocket {SocketUrl}")]
    public static partial void HandleDisconnectWhenDisconnectingInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 340,
        Level = LogLevel.Information,
        Message = "Handle disconnect semaphore canceled at WebSocket {SocketUrl}")]
    public static partial void HandleDisconnectSemaphoreCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 350,
        Level = LogLevel.Error,
        Message = "JSON-RPC 2.0 response ID missing at WebSocket {SocketUrl}: {TextResponse}")]
    public static partial void JsonRpc2ResponseIdMissingError(
        this ILogger logger, string socketUrl, string textResponse);

    [LoggerMessage(
        EventId = 360,
        Level = LogLevel.Warning,
        Message = "Pending JSON-RPC 2.0 request not found at WebSocket {SocketUrl} (RequestId: {RequestId}): {TextResponse}")]
    public static partial void PendingRequestNotFoundWarning(
        this ILogger logger, string socketUrl, string requestId, string textResponse);

    [LoggerMessage(
        EventId = 370,
        Level = LogLevel.Warning,
        Message = "Pending JSON-RPC 2.0 request null at WebSocket {SocketUrl} (RequestId: {RequestId}): {TextResponse}")]
    public static partial void PendingRequestNullWarning(
        this ILogger logger, string socketUrl, string requestId, string textResponse);

    [LoggerMessage(
        EventId = 380,
        Level = LogLevel.Error,
        Message = "JSON-RPC 2.0 response return error at WebSocket {SocketUrl} (RequestId: {RequestId}): {TextResponse}")]
    public static partial void JsonRpc2ResponseReturnError(
        this ILogger logger, string socketUrl, string requestId, string textResponse);

    [LoggerMessage(
        EventId = 390,
        Level = LogLevel.Debug,
        Message = "Received {MessageType} message at WebSocket {SocketUrl} with size of {MessageSize} bytes")]
    public static partial void ReceivedMessageDebug(
        this ILogger logger, string messageType, string socketUrl, long messageSize);

    [LoggerMessage(
        EventId = 400,
        Level = LogLevel.Error,
        Message = "Null or whitespace message received at WebSocket {SocketUrl}")]
    public static partial void NullOrWhitespaceMessageReceivedError(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 410,
        Level = LogLevel.Error,
        Message = "Deserialize text message received exception at WebSocket {SocketUrl} for `{TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ParseTextMessageReceivedException(
        this ILogger logger, string socketUrl, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 420,
        Level = LogLevel.Error,
        Message = "Deserialize text message received error at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void ParseTextMessageReceivedError(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 430,
        Level = LogLevel.Error,
        Message = "Process JSON-RPC 2.0 response exception at WebSocket {SocketUrl} (RequestId: {id} and Message: {TextMessage}) with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ProcessJsonRpc2ResponseException(
        this ILogger logger, string socketUrl, string id, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 440,
        Level = LogLevel.Information,
        Message = "Write receive text channel canceled at WebSocket {SocketUrl}")]
    public static partial void WriteReceiveTextChannelCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 450,
        Level = LogLevel.Error,
        Message = "Write receive text channel exception at WebSocket {SocketUrl} (Message: {TextMessage}) with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void WriteReceiveTextChannelException(
        this ILogger logger, string socketUrl, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 460,
        Level = LogLevel.Information,
        Message = "Write receive binary channel canceled at WebSocket {SocketUrl}")]
    public static partial void WriteReceiveBinaryChannelCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 470,
        Level = LogLevel.Error,
        Message = "Write receive binary channel exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void WriteReceiveBinaryChannelException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 480,
        Level = LogLevel.Error,
        Message = "Process received {MessageType} message exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ProcessReceiveMessageException(
        this ILogger logger, string messageType, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 490,
        Level = LogLevel.Warning,
        Message = "Received close message at WebSocket {SocketUrl}")]
    public static partial void ReceivedCloseMessageWarning(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 500,
        Level = LogLevel.Warning,
        Message = "Received message exceed max allowed size of {MaxAllowedByteSize} bytes at WebSocket {SocketUrl}: {messageByteSize} bytes")]
    public static partial void ReceivedMessageExceedsMaxAllowedSizeWarning(
        this ILogger logger, long maxAllowedByteSize, string socketUrl, long messageByteSize);

    [LoggerMessage(
        EventId = 510,
        Level = LogLevel.Information,
        Message = "Message receiving canceled at WebSocket {SocketUrl}")]
    public static partial void MessagingReceivingCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 520,
        Level = LogLevel.Information,
        Message = "Message receiving canceled exception at WebSocket {SocketUrl}")]
    public static partial void MessagingReceivingCanceledExceptionInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 530,
        Level = LogLevel.Error,
        Message = "Receive WebSocket exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveWebSocketException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 540,
        Level = LogLevel.Error,
        Message = "Receive unexpected exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveUnexpectedException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 550,
        Level = LogLevel.Error,
        Message = "Add pending request exception at WebSocket {SocketUrl} (RequestId: {RequestId} and Method: {Method}) with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void AddPendingRequestException(
        this ILogger logger, string socketUrl, string requestId, string method, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 560,
        Level = LogLevel.Error,
        Message = "Add pending request error at WebSocket {SocketUrl} (RequestId: {RequestId} and Method: {Method})")]
    public static partial void AddPendingRequestError(
        this ILogger logger, string socketUrl, string requestId, string method);

    [LoggerMessage(
        EventId = 570,
        Level = LogLevel.Information,
        Message = "Wait for JSON-RPC 2.0 response canceled at WebSocket {SocketUrl} (RequestId: {RequestId} and Method: {Method})")]
    public static partial void WaitForJsonRpc2ResponseCanceledInfo(
        this ILogger logger, string socketUrl, string requestId, string method);

    [LoggerMessage(
        EventId = 580,
        Level = LogLevel.Error,
        Message = "Wait for JSON-RPC 2.0 response exception at WebSocket {SocketUrl} (RequestId: {RequestId} and Method: {Method}) with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void WaitForJsonRpc2ResponseException(
        this ILogger logger, string socketUrl, string requestId, string method, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 590,
        Level = LogLevel.Information,
        Message = "Send WebSocket at {SocketUrl} is not connected")]
    public static partial void SendTextMessageWebSocketNotConnectedInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 600,
        Level = LogLevel.Error,
        Message = "Serialization unexpected exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SendTextMessageSerializationException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 610,
        Level = LogLevel.Debug,
        Message = "Sending message at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void SendingTextMessageDebug(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 620,
        Level = LogLevel.Information,
        Message = "Wait to send message canceled at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void WaitToSendTextMessageCanceledInfo(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 630,
        Level = LogLevel.Error,
        Message = "Wait to send message exception at WebSocket {SocketUrl} for `{TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void WaitToSendTextMessageException(
        this ILogger logger, string socketUrl, string textMessage, string exceptionMEssage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 640,
        Level = LogLevel.Information,
        Message = "Send message canceled at WebSocket {SocketUrl}: {TextMessage}")]
    public static partial void SendTextMessageCanceledInfo(
        this ILogger logger, string socketUrl, string textMessage);

    [LoggerMessage(
        EventId = 650,
        Level = LogLevel.Error,
        Message = "Send exception at WebSocket {SocketUrl} for `{TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SendTextMessageException(
        this ILogger logger, string socketUrl, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 660,
        Level = LogLevel.Information,
        Message = "Health check task shutdown timeout at WebSocket {SocketUrl}")]
    public static partial void HealthCheckTaskShutdownTimeoutInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 670,
        Level = LogLevel.Error,
        Message = "Health check task shutdown exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void HealthCheckTaskShutdownException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 680,
        Level = LogLevel.Information,
        Message = "Receive message task shutdown timeout at WebSocket {SocketUrl}")]
    public static partial void ReceiveMessageTaskShutdownTimeoutInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 690,
        Level = LogLevel.Error,
        Message = "Receive message task shutdown exception at WebSocket {SocketUrl} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveMessageTaskShutdownException(
        this ILogger logger, string socketUrl, string exceptionMessage, string? innerException, string? stackTrace);
}
