using Dalmarkit.Common.Dtos.RequestDtos;

namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IPublicClientWebSocketService : IDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    List<string> GetSubscribedChannels();
    Task SendNotificationAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default);
    Task<TResponse?> SendRequestAsync<TParams, TResponse>(JsonRpc2RequestDto<TParams> request, CancellationToken cancellationToken = default)
        where TResponse : class;
}
