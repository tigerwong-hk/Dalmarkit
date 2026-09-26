using Dalmarkit.Common.Dtos.RequestDtos;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IWebSocketClient : IDisposable
{
    string? WebSocketName { get; }

    bool IsConnectionConnected { get; }
    bool HasReachedMaxReconnectAttempts { get; }
    WebSocketConnectionState ConnectionState { get; }

    ChannelReader<WebSocketReceivedMessage<ReadOnlyMemory<byte>>> BinaryMessageReader { get; }
    ChannelReader<WebSocketReceivedMessage<JsonNode>> TextMessageReader { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(Func<string>? getWebSocketServerUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects, calling the factory before every attempt (the first connect and each reconnect) for the server URL.
    /// The factory receives the attempt's connection-timeout token; an exception or timeout fails that attempt.
    /// The URL it returns is never logged, so it may carry a credential.
    /// </summary>
    /// <param name="getWebSocketServerUrlAsync">Returns the server URL for an attempt; null uses ServerUrl from the options</param>
    /// <param name="cancellationToken">Cancels the first connect</param>
    Task ConnectAsync(Func<WebSocketConnectAttempt, CancellationToken, ValueTask<string>>? getWebSocketServerUrlAsync, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<TResponse?> SendJsonRpc2RequestAsync<TParams, TResponse>(JsonRpc2RequestDto<TParams> request, CancellationToken cancellationToken = default)
        where TResponse : class;
    Task SendNotificationAsync<TMessage>(TMessage messageObject, CancellationToken cancellationToken = default);
    Task<TResponse?> SendRequestAsync<TRequest, TResponse>(string requestId, TRequest request, CancellationToken cancellationToken = default)
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
