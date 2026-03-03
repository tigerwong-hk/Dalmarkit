using Dalmarkit.Common.PubSub;
using Dalmarkit.Common.PubSub.SignalR;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Immutable;

namespace Dalmarkit.AspNetCore.PubSub.SignalR;

public abstract class SignalRTopicHubBase(ITopicSubscriptionService subscriptionService) : Hub<ISignalRDataClient>
{
    private readonly ITopicSubscriptionService _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));

    public virtual ImmutableHashSet<string> GetTopicSubscriptions()
    {
        return _subscriptionService.GetSubscriberTopics(Context.ConnectionId);
    }

    public virtual async Task<ImmutableHashSet<string>> RemoveConnectionAsync(string connectionId)
    {
        ImmutableHashSet<string> topicsRemoved = _subscriptionService.RemoveSubscriber(Context.ConnectionId, GetTopicPrefix);
        foreach (string topic in topicsRemoved)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, topic);
        }

        return topicsRemoved;
    }

    public virtual async Task<bool> SubscribeTopicAsync(string topic)
    {
        bool isSubscribed = _subscriptionService.SubscribeTopic(Context.ConnectionId, topic, GetTopicPrefix);
        if (isSubscribed)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, topic);
        }

        return isSubscribed;
    }

    public virtual async Task<bool> UnsubscribeTopicAsync(string topic)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, topic);
        return _subscriptionService.UnsubscribeTopic(Context.ConnectionId, topic, GetTopicPrefix);
    }

    protected abstract string GetTopicPrefix(string topicName);
}
