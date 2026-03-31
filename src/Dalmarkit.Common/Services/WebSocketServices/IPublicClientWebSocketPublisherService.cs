namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IPublicClientWebSocketPublisherService : IPublicClientWebSocketService
{
    Task<List<string>> SubscribeChannelsAsync(List<string> channelNames, CancellationToken cancellationToken = default);
    Task<List<string>> UnsubscribeChannelsAsync(List<string> channelNames, CancellationToken cancellationToken = default);
}
