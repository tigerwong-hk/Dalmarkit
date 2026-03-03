using System.Threading.Channels;
using Dalmarkit.Common.Dtos.RequestDtos;

namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IWebSocketClient : IDisposable
{
    bool IsConnected { get; }
    bool HasReachedMaxReconnectAttempts { get; }
    WebSocketConnectionState ConnectionState { get; }

    ChannelReader<WebSocketReceivedMessage<ReadOnlyMemory<byte>>> BinaryMessageReader { get; }
    ChannelReader<WebSocketReceivedMessage<string>> TextMessageReader { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(Func<string>? getWebSocketServerUrl, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendJsonRpc2NotificationAsync<TMessage>(TMessage messageObject, CancellationToken cancellationToken = default);
    Task<TResponse?> SendJsonRpc2RequestAsync<TParams, TResponse>(JsonRpc2RequestDto<TParams> request, CancellationToken cancellationToken = default)
        where TResponse : class;

    enum WebSocketConnectionState
    {
        Disconnected = 10,
        Connecting = 20,
        Connected = 30,
        Reconnecting = 40,
        Disconnecting = 50
    }
}

public readonly struct WebSocketReceivedMessage<TData>
{
    public required TData Data { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
}
