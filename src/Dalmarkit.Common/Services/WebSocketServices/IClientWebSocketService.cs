using Dalmarkit.Common.Dtos.RequestDtos;
using static Dalmarkit.Common.Services.WebSocketServices.IWebSocketClient;

namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IClientWebSocketService : IDisposable
{
    string? AuthenticationId { get; }
    bool IsAuthenticated { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(string? authenticationId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    WebSocketAuthenticationState GetAuthenticationState();
    WebSocketConnectionState GetConnectionState();
    List<string> GetSubscribedChannels();
    Task SendNotificationAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default);
    Task<TResponse?> SendRequestAsync<TParams, TResponse>(JsonRpc2RequestDto<TParams> request, CancellationToken cancellationToken = default)
        where TResponse : class;
}

public enum WebSocketAuthenticationState
{
    Unauthenticated = 1000,
    AuthenticationSuccessful = 2000,
    AuthenticationFailed = 3000,
    Disconnected = 4000
}
