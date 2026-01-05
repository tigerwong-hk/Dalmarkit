using System.Net.WebSockets;

namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IWebSocketClient : IDisposable
{
    bool IsConnected { get; }
    bool HasReachedMaxReconnectAttempts { get; }

    event EventHandler<byte[]>? OnBinaryMessageReceived;
    event EventHandler? OnHealthCheckCanceled;
    event EventHandler<Exception>? OnHealthCheckException;
    event EventHandler<int>? OnMaxReconnectionAttemptsReached;
    event EventHandler<Exception>? OnNoServerHeartbeatDisconnectException;
    event EventHandler<long>? OnNoServerHeartbeatReceived;
    event EventHandler<Exception>? OnProcessReceivedMessageException;
    event EventHandler<Exception>? OnReceiveUnexpectedException;
    event EventHandler<WebSocketException>? OnReceiveWebSocketException;
    event EventHandler? OnReconnectCanceled;
    event EventHandler<string>? OnReconnectError;
    event EventHandler<ReconnectExceptionEvent>? OnReconnectException;
    event EventHandler? OnShutdownCheckHealthTaskTimeout;
    event EventHandler<Exception>? OnShutdownCheckHealthTaskException;
    event EventHandler? OnShutdownReceiveMessageTaskTimeout;
    event EventHandler<Exception>? OnShutdownReceiveMessageTaskException;
    event EventHandler<string>? OnTextMessageReceived;
    event EventHandler? OnWebSocketConnected;
    event EventHandler<string?>? OnWebSocketDisconnected;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default);

    enum WebSocketConnectionState
    {
        Disconnected = 10,
        Connecting = 20,
        Connected = 30,
        Reconnecting = 40,
        Disconnecting = 50
    }

    class ReconnectExceptionEvent
    {
        public int ReconnectAttempts { get; set; }
        public Exception ReconnectException { get; set; } = null!;
    }
}
