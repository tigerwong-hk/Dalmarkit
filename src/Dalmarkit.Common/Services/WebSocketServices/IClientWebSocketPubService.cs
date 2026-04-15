namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IClientWebSocketPubService : IClientWebSocketService
{
    Task<List<string>> SubscribeChannelsAsync(List<string> channelNames, CancellationToken cancellationToken = default);
    Task<List<string>> UnsubscribeChannelsAsync(List<string> channelNames, CancellationToken cancellationToken = default);
}
