using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dalmarkit.Common.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Dalmarkit.Common.Services.WebSocketServices.IWebSocketClient;
using static Dalmarkit.Common.Services.WebSocketServices.WebSocketClientEvents;

namespace Dalmarkit.Common.Services.WebSocketServices;

public class WebSocketClient : IWebSocketClient
{
    private readonly IEventDispatcher _eventDispatcher;
    private readonly WebSocketClientOptions _options;
    private readonly ILogger<WebSocketClient> _logger;

    private readonly CancellationTokenSource _disposalCts = new();

    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

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

    public WebSocketConnectionState State => _connectionState;

    public static readonly JsonSerializerOptions JsonWebOptions = new(JsonSerializerDefaults.Web);

    public WebSocketClient(
        IEventDispatcher eventDispatcher,
        IOptions<WebSocketClientOptions> options,
        ILogger<WebSocketClient> logger)
    {
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
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

        _checkHealthTask = null;
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

    public virtual async Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!IsConnected)
        {
            _logger.SendWebSocketNotConnectedInfo(_options.ServerUrl);
            throw new InvalidOperationException("WebSocket is not connected");
        }

        string json;
        byte[] bytes;

        try
        {
            json = JsonSerializer.Serialize(message, JsonWebOptions);
            bytes = Encoding.UTF8.GetBytes(json);
        }
        catch (Exception ex)
        {
            _logger.SerializationUnexpectedException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }

        _logger.SendingMessageDebug(_options.ServerUrl, json);

        await _sendSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using CancellationTokenSource requestTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.RequestTimeoutMilliseconds > 0)
            {
                requestTimeoutCts.CancelAfter(_options.RequestTimeoutMilliseconds);
            }
            await _clientWebSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, requestTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            _logger.SendWebSocketException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }
        catch (Exception ex)
        {
            _logger.SendUnexpectedException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            _ = _sendSemaphore.Release();
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
            return;
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

                _logger.ReconnectedToWebSocketInfo(_options.ServerUrl, _reconnectAttempts);
                return;
            }
            catch (OperationCanceledException)
            {
                _connectionState = WebSocketConnectionState.Disconnected;
                _logger.ReconnectCanceledInfo(_options.ServerUrl);
                await _eventDispatcher.DispatchEventAsync(new OnReconnectCanceled(), CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.ReconnectFailedException(_options.ServerUrl, _reconnectAttempts, ex.Message, ex.InnerException?.Message, ex.StackTrace);
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

        _logger.ConnectingToWebSocketInfo(_options.ServerUrl);

        try
        {
            await _clientWebSocket.ConnectAsync(new Uri(_options.ServerUrl), connectionTimeoutCts.Token).ConfigureAwait(false);

            _connectionState = WebSocketConnectionState.Connected;
            _reconnectAttempts = 0;

            _logger.ConnectedToWebSocketInfo(_options.ServerUrl);
            await _eventDispatcher.DispatchEventAsync(new OnWebSocketConnected(), cancellationToken).ConfigureAwait(false);
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

            _lastReceivedTimestampMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _checkHealthTask = CheckHealthAsync(_checkHealthCts.Token);
        }

        _receiveMessageCts?.Dispose();
        _receiveMessageCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);

        _receiveMessageTask = ReceiveMessagesAsync(_receiveMessageCts.Token);
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
            return;
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

    protected virtual async Task ProcessReceivedMessageAsync(WebSocketMessageType messageType, byte[] fullMessage, CancellationToken cancellationToken = default)
    {
        _logger.ReceivedMessageDebug(messageType.ToString(), _options.ServerUrl, fullMessage.Length);

        try
        {
            if (messageType == WebSocketMessageType.Text)
            {
                string textMessage = Encoding.UTF8.GetString(fullMessage);
                await _eventDispatcher.DispatchEventAsync(new OnTextMessageReceived(textMessage), cancellationToken).ConfigureAwait(false);
            }
            else if (messageType == WebSocketMessageType.Binary)
            {
                await _eventDispatcher.DispatchEventAsync(new OnBinaryMessageReceived(fullMessage), cancellationToken).ConfigureAwait(false);
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
            _receiveMessageTask = null;
        }
    }
}

public static partial class WebSocketClientLogs
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "Connect not disconnected WebSocket at {SocketUrl}")]
    public static partial void ConnectNotDisconnectedWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Information,
        Message = "Send WebSocket at {SocketUrl} is not connected")]
    public static partial void SendWebSocketNotConnectedInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Error,
        Message = "Serialization unexpected exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SerializationUnexpectedException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Debug,
        Message = "Sending message for WebSocket at {SocketUrl}: {Message}")]
    public static partial void SendingMessageDebug(
        this ILogger logger, string socketUrl, string message);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Error,
        Message = "Send WebSocket exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SendWebSocketException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Error,
        Message = "Send unexpected exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SendUnexpectedException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Information,
        Message = "Not reconnecting as no reconnection settings for WebSocket at {SocketUrl}")]
    public static partial void NoReconnectionSettingsInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Information,
        Message = "Reconnect not disconnected WebSocket at {SocketUrl}")]
    public static partial void ReconnectNotDisconnectedWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Information,
        Message = "Reconnect semaphore canceled for WebSocket at {SocketUrl}")]
    public static partial void ReconnectSemaphoreCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Error,
        Message = "Reconnect semaphore exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReconnectSemaphoreException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 110,
        Level = LogLevel.Information,
        Message = "Reconnecting WebSocket at {SocketUrl} in {DelayMilliseconds} ms (attempt {ReconnectAttempts} of {MaxAttempts})")]
    public static partial void ReconnectingWebSocketDelayInfo(
        this ILogger logger, string socketUrl, int delayMilliseconds, int reconnectAttempts, int maxAttempts);

    [LoggerMessage(
        EventId = 120,
        Level = LogLevel.Information,
        Message = "Reconnected to WebSocket at {SocketUrl} after {ReconnectAttempts}")]
    public static partial void ReconnectedToWebSocketInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts);

    [LoggerMessage(
        EventId = 130,
        Level = LogLevel.Information,
        Message = "Reconnection canceled for WebSocket at {SocketUrl}")]
    public static partial void ReconnectCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 140,
        Level = LogLevel.Error,
        Message = "Reconnection failed for WebSocket at {SocketUrl} on attempt {ReconnectAttempts} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReconnectFailedException(
        this ILogger logger, string socketUrl, int reconnectAttempts, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 150,
        Level = LogLevel.Error,
        Message = "Max reconnection attempts reached for WebSocket at {SocketUrl}: {ReconnectAttempts}")]
    public static partial void MaxReconnectionAttemptsReachedError(
        this ILogger logger, string socketUrl, int reconnectAttempts);

    [LoggerMessage(
        EventId = 160,
        Level = LogLevel.Warning,
        Message = "No server heartbeat received for WebSocket at {SocketUrl} since {LastReceivedUtcDateTime}")]
    public static partial void NoServerHeartbeatWarning(
        this ILogger logger, string socketUrl, string lastReceivedUtcDateTime);

    [LoggerMessage(
        EventId = 170,
        Level = LogLevel.Information,
        Message = "Health check canceled for WebSocket at {SocketUrl}")]
    public static partial void HealthCheckCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 180,
        Level = LogLevel.Information,
        Message = "Health check canceled exception for WebSocket at {SocketUrl}")]
    public static partial void HealthCheckCanceledExceptionInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 190,
        Level = LogLevel.Error,
        Message = "Health check unexpected exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void HealthCheckUnexpectedException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Information,
        Message = "Connecting to WebSocket at {SocketUrl}")]
    public static partial void ConnectingToWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 210,
        Level = LogLevel.Information,
        Message = "Connected to WebSocket at {SocketUrl}")]
    public static partial void ConnectedToWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 220,
        Level = LogLevel.Information,
        Message = "Connect canceled for WebSocket at {SocketUrl}")]
    public static partial void ConnectCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 230,
        Level = LogLevel.Error,
        Message = "Connection failed for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ConnectFailedException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 240,
        Level = LogLevel.Error,
        Message = "Disconnect disconnecting or disconnected WebSocket at {SocketUrl}")]
    public static partial void DisconnectNotConnectedWebSocketError(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 250,
        Level = LogLevel.Information,
        Message = "Disconnect semaphore canceled for WebSocket at {SocketUrl}")]
    public static partial void DisconnectSemaphoreCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 260,
        Level = LogLevel.Error,
        Message = "Disconnect semaphore exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void DisconnectSemaphoreException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

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
        Message = "Disconnect canceled for WebSocket at {SocketUrl}")]
    public static partial void DisconnectCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 310,
        Level = LogLevel.Error,
        Message = "Disconnection failed for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void DisconnectException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 320,
        Level = LogLevel.Information,
        Message = "Handle disconnect when disposed for WebSocket at {SocketUrl}")]
    public static partial void HandleDisconnectWhenDisposedInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 330,
        Level = LogLevel.Information,
        Message = "Handle disconnect when disconnecting for WebSocket at {SocketUrl}")]
    public static partial void HandleDisconnectWhenDisconnectingInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 340,
        Level = LogLevel.Information,
        Message = "Handle disconnect semaphore canceled for WebSocket at {SocketUrl}")]
    public static partial void HandleDisconnectSemaphoreCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 350,
        Level = LogLevel.Debug,
        Message = "Received {MessageType} message for WebSocket at {SocketUrl} with size of {MessageSize} bytes")]
    public static partial void ReceivedMessageDebug(
        this ILogger logger, string messageType, string socketUrl, long messageSize);

    [LoggerMessage(
        EventId = 360,
        Level = LogLevel.Error,
        Message = "Process received {MessageType} message exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ProcessReceiveMessageException(
        this ILogger logger, string messageType, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 370,
        Level = LogLevel.Warning,
        Message = "Received close message for WebSocket at {SocketUrl}")]
    public static partial void ReceivedCloseMessageWarning(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 380,
        Level = LogLevel.Warning,
        Message = "Received message exceed max allowed size of {MaxAllowedByteSize} bytes for WebSocket at {SocketUrl}: {messageByteSize} bytes")]
    public static partial void ReceivedMessageExceedsMaxAllowedSizeWarning(
        this ILogger logger, long maxAllowedByteSize, string socketUrl, long messageByteSize);

    [LoggerMessage(
        EventId = 390,
        Level = LogLevel.Information,
        Message = "Message receiving canceled for WebSocket at {SocketUrl}")]
    public static partial void MessagingReceivingCanceledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 400,
        Level = LogLevel.Information,
        Message = "Message receiving canceled exception for WebSocket at {SocketUrl}")]
    public static partial void MessagingReceivingCanceledExceptionInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 410,
        Level = LogLevel.Error,
        Message = "Receive WebSocket exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveWebSocketException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 420,
        Level = LogLevel.Error,
        Message = "Receive unexpected exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveUnexpectedException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 430,
        Level = LogLevel.Information,
        Message = "Health check task shutdown timeout for WebSocket at {SocketUrl}")]
    public static partial void HealthCheckTaskShutdownTimeoutInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 440,
        Level = LogLevel.Error,
        Message = "Health check task shutdown exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void HealthCheckTaskShutdownException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 450,
        Level = LogLevel.Information,
        Message = "Receive message task shutdown timeout for WebSocket at {SocketUrl}")]
    public static partial void ReceiveMessageTaskShutdownTimeoutInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 460,
        Level = LogLevel.Error,
        Message = "Receive message task shutdown exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveMessageTaskShutdownException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);
}
