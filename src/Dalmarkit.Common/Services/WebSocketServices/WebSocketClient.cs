using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Dalmarkit.Common.Services.WebSocketServices.IWebSocketClient;

namespace Dalmarkit.Common.Services.WebSocketServices;

public abstract class WebSocketClient : IWebSocketClient
{
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

    public event EventHandler<byte[]>? OnBinaryMessageReceived;
    public event EventHandler<Exception>? OnConnectException;
    public event EventHandler<Exception>? OnDisconnectException;
    public event EventHandler<Exception>? OnProcessReceivedMessageException;
    public event EventHandler<Exception>? OnReceiveUnexpectedException;
    public event EventHandler<WebSocketException>? OnReceiveWebSocketException;
    public event EventHandler<Exception>? OnReconnectException;
    public event EventHandler<Exception>? OnSendUnexpectedException;
    public event EventHandler<WebSocketException>? OnSendWebSocketException;
    public event EventHandler<string>? OnTextMessageReceived;
    public event EventHandler? OnWebSocketConnected;
    public event EventHandler<string?>? OnWebSocketDisconnected;

    public static readonly JsonSerializerOptions JsonWebOptions = new(JsonSerializerDefaults.Web);

    protected WebSocketClient(
        IOptions<WebSocketClientOptions> options,
        ILogger<WebSocketClient> logger)
    {
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

        if (Interlocked.CompareExchange(ref _connectionState, WebSocketConnectionState.Connecting, WebSocketConnectionState.Disconnected) != WebSocketConnectionState.Disconnected)
        {
            _logger.ConnectNotDisconnectedWebSocketInfo(_options.ServerUrl);
            throw new InvalidOperationException("WebSocket is not disconnected");
        }

        await ConnectInternalAsync(cancellationToken).ConfigureAwait(false);
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
            OnSendUnexpectedException?.Invoke(this, ex);
            throw;
        }

        _logger.SendingMessageDebug(_options.ServerUrl, json);

        await _sendSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using CancellationTokenSource requestTimeoutCts = new(TimeSpan.FromMilliseconds(_options.RequestTimeoutMilliseconds));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestTimeoutCts.Token);
            await _clientWebSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, linkedCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            _logger.SendWebSocketException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            OnSendWebSocketException?.Invoke(this, ex);
            throw;
        }
        catch (Exception ex)
        {
            _logger.SendUnexpectedException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            OnSendUnexpectedException?.Invoke(this, ex);
            throw;
        }
        finally
        {
            _ = _sendSemaphore.Release();
        }
    }

    private async Task AttemptReconnectAsync(CancellationToken cancellationToken = default)
    {
        WebSocketClientOptions.ReconnectionPolicy? policy = _options.Reconnection;
        if (policy == null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _connectionState, WebSocketConnectionState.Reconnecting, WebSocketConnectionState.Disconnected) != WebSocketConnectionState.Disconnected)
        {
            _logger.ReconnectNotDisconnectedWebSocketInfo(_options.ServerUrl);
            throw new InvalidOperationException("WebSocket is not disconnected");
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
                _logger.ReconnectionCancelledInfo(_options.ServerUrl);
                return;
            }
            catch (Exception ex)
            {
                _logger.ReconnectionFailedForWebSocketException(_options.ServerUrl, _reconnectAttempts, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                OnReconnectException?.Invoke(this, ex);
            }
        }

        _connectionState = WebSocketConnectionState.Disconnected;
        _logger.MaxReconnectionAttemptsReachedError(_options.ServerUrl, _reconnectAttempts);
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
                    _logger.NoServerHeartbeatWarning(_options.ServerUrl, _lastReceivedTimestampMilliseconds);
                    await DisconnectInternalAsync(WebSocketCloseStatus.EndpointUnavailable, "No server heartbeat", false, true, cancellationToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.HealthCheckCancelledInfo(_options.ServerUrl);
        }
        catch (Exception ex)
        {
            _logger.HealthCheckUnexpectedException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
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
        connectionTimeoutCts.CancelAfter(_options.ConnectionTimeoutMilliseconds);

        _logger.ConnectingToWebSocketInfo(_options.ServerUrl);

        try
        {
            await _clientWebSocket.ConnectAsync(new Uri(_options.ServerUrl), connectionTimeoutCts.Token).ConfigureAwait(false);

            _connectionState = WebSocketConnectionState.Connected;
            _reconnectAttempts = 0;

            _logger.ConnectedToWebSocketInfo(_options.ServerUrl);
            OnWebSocketConnected?.Invoke(this, EventArgs.Empty);

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
        catch (Exception ex)
        {
            _logger.ConnectionFailedForWebSocketException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            OnConnectException?.Invoke(this, ex);
            throw;
        }
    }

    protected virtual async Task DisconnectInternalAsync(WebSocketCloseStatus closeStatus, string? statusDescription, bool shutdownCheckHealthTask, bool shutdownReceiveMessageTask, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _connectionState, WebSocketConnectionState.Disconnecting, WebSocketConnectionState.Connected) != WebSocketConnectionState.Connected)
        {
            _logger.DisconnectNotConnectedWebSocketError(_options.ServerUrl);
            throw new InvalidOperationException("WebSocket is not connected");
        }

        try
        {
            if (shutdownCheckHealthTask)
            {
                await ShutdownCheckHealthTask(cancellationToken).ConfigureAwait(false);
            }

            if (shutdownReceiveMessageTask)
            {
                await ShutdownReceiveMessageTask(cancellationToken).ConfigureAwait(false);
            }

            if (IsConnected)
            {
                using CancellationTokenSource disconnectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                disconnectTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_options.GracefulShutdownTimeoutMilliseconds));

                _logger.DisconnectingFromWebSocketInfo(_options.ServerUrl);

                try
                {
                    await _clientWebSocket!.CloseAsync(closeStatus, statusDescription, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.DisconnectionFailedForWebSocketException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                    OnDisconnectException?.Invoke(this, ex);
                    throw;
                }

                _logger.DisconnectedFromWebSocketInfo(_options.ServerUrl);
                OnWebSocketDisconnected?.Invoke(this, statusDescription);
            }
            else
            {
                _logger.DisconnectUnconnectedWebSocketWarning(_options.ServerUrl);
            }
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
        if (_isDisposed || _connectionState == WebSocketConnectionState.Disconnecting)
        {
            return;
        }

        await ShutdownCheckHealthTask(cancellationToken).ConfigureAwait(false);

        _connectionState = WebSocketConnectionState.Disconnected;

        OnWebSocketDisconnected?.Invoke(this, statusDescription);

        _clientWebSocket?.Dispose();
        _clientWebSocket = null;

        if (!cancellationToken.IsCancellationRequested)
        {
            await AttemptReconnectAsync(_disposalCts.Token).ConfigureAwait(false);
        }
    }

    private async Task ProcessReceivedMessageAsync(WebSocketMessageType messageType, byte[] fullMessage)
    {
        _logger.ReceivedMessageDebug(messageType.ToString(), _options.ServerUrl, fullMessage.Length);

        try
        {
            if (messageType == WebSocketMessageType.Text)
            {
                string textMessage = Encoding.UTF8.GetString(fullMessage);
                OnTextMessageReceived?.Invoke(this, textMessage);
            }
            else if (messageType == WebSocketMessageType.Binary)
            {
                OnBinaryMessageReceived?.Invoke(this, fullMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.ProcessReceiveMessageException(_options.ServerUrl, messageType.ToString(), ex.Message, ex.InnerException?.Message, ex.StackTrace);
            OnProcessReceivedMessageException?.Invoke(this, ex);
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

                    await HandleDisconnectionAsync("Close message received", cancellationToken);
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

                    await ProcessReceivedMessageAsync(result.MessageType, fullMessage).ConfigureAwait(false);
                }
            }

            if (IsConnected)
            {
                await DisconnectInternalAsync(WebSocketCloseStatus.NormalClosure, "Receiving cancelled", true, false, cancellationToken);
            }
            else
            {
                await HandleDisconnectionAsync("WebSocket not connected", cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.MessagingReceivingCancelledInfo(_options.ServerUrl);
            await DisconnectInternalAsync(WebSocketCloseStatus.NormalClosure, "Receiving cancelled exception", true, false, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.ReceiveWebSocketException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            OnReceiveWebSocketException?.Invoke(this, ex);
            await HandleDisconnectionAsync("WebSocket exception while receiving", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.ReceiveUnexpectedException(_options.ServerUrl, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            OnReceiveUnexpectedException?.Invoke(this, ex);
            await HandleDisconnectionAsync("Unexpected exception while receiving", cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected virtual async Task ShutdownCheckHealthTask(CancellationToken cancellationToken = default)
    {
        try
        {
            _checkHealthCts?.Cancel();

            if (_checkHealthTask != null)
            {
                try
                {
                    await _checkHealthTask.WaitAsync(TimeSpan.FromMilliseconds(_options.GracefulShutdownTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (TimeoutException)
                {
                    _logger.HealthCheckTaskShutdownTimeoutInfo(_options.ServerUrl);
                }
            }
        }
        finally
        {
            _checkHealthCts?.Dispose();
            _checkHealthCts = null;
            _checkHealthTask = null;
        }
    }

    protected virtual async Task ShutdownReceiveMessageTask(CancellationToken cancellationToken = default)
    {
        try
        {
            _receiveMessageCts?.Cancel();

            if (_receiveMessageTask != null)
            {
                try
                {
                    await _receiveMessageTask.WaitAsync(TimeSpan.FromMilliseconds(_options.GracefulShutdownTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (TimeoutException)
                {
                    _logger.ReceiveMessageTaskShutdownTimeoutInfo(_options.ServerUrl);
                }
            }
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
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Reconnect not disconnected WebSocket at {SocketUrl}")]
    public static partial void ReconnectNotDisconnectedWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Reconnected to WebSocket at {SocketUrl} after {ReconnectAttempts}")]
    public static partial void ReconnectedToWebSocketInfo(
        this ILogger logger, string socketUrl, int reconnectAttempts);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Reconnection cancelled for WebSocket at {SocketUrl}")]
    public static partial void ReconnectionCancelledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Error,
        Message = "Reconnection failed for WebSocket at {SocketUrl} on attempt {ReconnectAttempt} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReconnectionFailedForWebSocketException(
        this ILogger logger, string socketUrl, int reconnectAttempt, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Error,
        Message = "Max reconnection attempts reached for WebSocket at {SocketUrl}: {ReconnectAttempt}")]
    public static partial void MaxReconnectionAttemptsReachedError(
        this ILogger logger, string socketUrl, int reconnectAttempt);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Information,
        Message = "Connect not disconnected WebSocket at {SocketUrl}")]
    public static partial void ConnectNotDisconnectedWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "Connecting to WebSocket at {SocketUrl}")]
    public static partial void ConnectingToWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Information,
        Message = "Connected not connecting WebSocket at {SocketUrl}")]
    public static partial void ConnectedNotConnectingWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Information,
        Message = "Connected to WebSocket at {SocketUrl}")]
    public static partial void ConnectedToWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Error,
        Message = "Connection failed for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ConnectionFailedForWebSocketException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Error,
        Message = "Disconnect disconnecting or disconnected WebSocket at {SocketUrl}")]
    public static partial void DisconnectNotConnectedWebSocketError(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 45,
        Level = LogLevel.Information,
        Message = "Disconnecting from WebSocket at {SocketUrl}")]
    public static partial void DisconnectingFromWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Information,
        Message = "Health check task shutdown timeout for WebSocket at {SocketUrl}")]
    public static partial void HealthCheckTaskShutdownTimeoutInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Information,
        Message = "Receive message task shutdown timeout for WebSocket at {SocketUrl}")]
    public static partial void ReceiveMessageTaskShutdownTimeoutInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Information,
        Message = "Disconnected WebSocket at {SocketUrl}")]
    public static partial void DisconnectedFromWebSocketInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Error,
        Message = "Disconnection failed for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void DisconnectionFailedForWebSocketException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Warning,
        Message = "Disconnect unconnected WebSocket at {SocketUrl}")]
    public static partial void DisconnectUnconnectedWebSocketWarning(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Warning,
        Message = "Received close message for WebSocket at {SocketUrl}")]
    public static partial void ReceivedCloseMessageWarning(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 110,
        Level = LogLevel.Warning,
        Message = "Received message exceed max allowed size of {MaxAllowedByteSize} bytes for WebSocket at {SocketUrl}: {messageByteSize} bytes")]
    public static partial void ReceivedMessageExceedsMaxAllowedSizeWarning(
        this ILogger logger, long maxAllowedByteSize, string socketUrl, long messageByteSize);

    [LoggerMessage(
        EventId = 120,
        Level = LogLevel.Debug,
        Message = "Received {MessageType} message for WebSocket at {SocketUrl} with size of {MessageSize} bytes")]
    public static partial void ReceivedMessageDebug(
        this ILogger logger, string messageType, string socketUrl, long messageSize);

    [LoggerMessage(
        EventId = 125,
        Level = LogLevel.Error,
        Message = "Process received {MessageType} message exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ProcessReceiveMessageException(
        this ILogger logger, string messageType, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 130,
        Level = LogLevel.Information,
        Message = "Message receiving cancelled for WebSocket at {SocketUrl}")]
    public static partial void MessagingReceivingCancelledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 140,
        Level = LogLevel.Error,
        Message = "Receive WebSocket exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveWebSocketException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 150,
        Level = LogLevel.Error,
        Message = "Receive unexpected exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveUnexpectedException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 160,
        Level = LogLevel.Information,
        Message = "Send WebSocket at {SocketUrl} is not connected")]
    public static partial void SendWebSocketNotConnectedInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 170,
        Level = LogLevel.Error,
        Message = "Serialization unexpected exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SerializationUnexpectedException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 180,
        Level = LogLevel.Debug,
        Message = "Sending message for WebSocket at {SocketUrl}: {Message}")]
    public static partial void SendingMessageDebug(
        this ILogger logger, string socketUrl, string message);

    [LoggerMessage(
        EventId = 190,
        Level = LogLevel.Error,
        Message = "Send WebSocket exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SendWebSocketException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Error,
        Message = "Send unexpected exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SendUnexpectedException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 210,
        Level = LogLevel.Information,
        Message = "Health check cancelled for WebSocket at {SocketUrl}")]
    public static partial void HealthCheckCancelledInfo(
        this ILogger logger, string socketUrl);

    [LoggerMessage(
        EventId = 220,
        Level = LogLevel.Error,
        Message = "Health check unexpected exception for WebSocket at {SocketUrl} with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void HealthCheckUnexpectedException(
        this ILogger logger, string socketUrl, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 230,
        Level = LogLevel.Information,
        Message = "Reconnecting WebSocket at {SocketUrl} in {ReconnectDelayMilliseconds} ms (attempt {ReconnectAttempts} of {MaxReconnectAttempts})")]
    public static partial void ReconnectingWebSocketDelayInfo(
        this ILogger logger, string socketUrl, long reconnectDelayMilliseconds, int reconnectAttempts, int maxReconnectAttempts);

    [LoggerMessage(
        EventId = 240,
        Level = LogLevel.Warning,
        Message = "No server heartbeat received for WebSocket at {SocketUrl} since epoch timestamp {LastReceivedTimestampMilliseconds} ms")]
    public static partial void NoServerHeartbeatWarning(
        this ILogger logger, string socketUrl, long lastReceivedTimestampMilliseconds);
}
