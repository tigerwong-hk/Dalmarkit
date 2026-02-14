using Dalmarkit.Common.PubSub;
using Dalmarkit.Common.PubSub.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace Dalmarkit.AspNetCore.PubSub.SignalR;

public class SignalRTopicHubBase(ITopicSubscriptionService subscriptionService) : Hub<ISignalRDataClient>
{
    private readonly ITopicSubscriptionService _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));

    public virtual async Task<IReadOnlyCollection<string>> GetTopicSubscriptionsAsync()
    {
        return await _subscriptionService.GetSubscribedTopicsByConnectionIdAsync(Context.ConnectionId);
    }

    public virtual async Task<IReadOnlyCollection<string>> RemoveConnectionAsync(string connectionId)
    {
        return await _subscriptionService.RemoveConnectionAsync(Context.ConnectionId);
    }

    public virtual async Task SubscribeTopicAsync(string topic)
    {
        await _subscriptionService.SubscribeTopicAsync(Context.ConnectionId, topic);
        await Groups.AddToGroupAsync(Context.ConnectionId, topic);
    }

    public virtual async Task UnsubscribeTopicAsync(string topic)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, topic);
        await _subscriptionService.UnsubscribeTopicAsync(Context.ConnectionId, topic);
    }
}
