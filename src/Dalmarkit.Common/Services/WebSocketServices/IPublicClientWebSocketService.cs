namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IPublicClientWebSocketService
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SubscribeChannelsAsync(List<string> channels, CancellationToken cancellationToken = default);
    Task UnsubscribeChannelsAsync(List<string> channels, CancellationToken cancellationToken = default);
}
