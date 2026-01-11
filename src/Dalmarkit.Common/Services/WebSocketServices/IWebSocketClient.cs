namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IWebSocketClient : IDisposable
{
    bool IsConnected { get; }
    bool HasReachedMaxReconnectAttempts { get; }

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
}
