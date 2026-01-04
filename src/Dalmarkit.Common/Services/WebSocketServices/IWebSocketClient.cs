using System.Net.WebSockets;

namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IWebSocketClient : IDisposable
{
    bool IsConnected { get; }
    bool HasReachedMaxReconnectAttempts { get; }

    event EventHandler<byte[]>? OnBinaryMessageReceived;
    event EventHandler<Exception>? OnConnectException;
    event EventHandler<Exception>? OnDisconnectException;
    event EventHandler<Exception>? OnProcessReceivedMessageException;
    event EventHandler<Exception>? OnReceiveUnexpectedException;
    event EventHandler<WebSocketException>? OnReceiveWebSocketException;
    event EventHandler<Exception>? OnReconnectException;
    event EventHandler<Exception>? OnSendUnexpectedException;
    event EventHandler<WebSocketException>? OnSendWebSocketException;
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
}
