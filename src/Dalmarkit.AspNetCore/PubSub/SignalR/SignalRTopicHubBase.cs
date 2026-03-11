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

    public virtual async Task<ImmutableHashSet<string>> RemoveConnectionAsync()
    {
        ImmutableHashSet<string> topicsRemoved = _subscriptionService.RemoveSubscriber(Context.ConnectionId, GetTopicPrefix);
        List<Task> removeFromGroupTasks = [];
        foreach (string topic in topicsRemoved)
        {
            removeFromGroupTasks.Add(Groups.RemoveFromGroupAsync(Context.ConnectionId, topic));
        }

        await Task.WhenAll(removeFromGroupTasks).ConfigureAwait(false);
        return topicsRemoved;
    }

    public virtual async Task<bool> SubscribeTopicAsync(string topic)
    {
        bool isSubscribed = _subscriptionService.SubscribeTopic(Context.ConnectionId, topic, GetTopicPrefix);
        if (isSubscribed)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, topic).ConfigureAwait(false);
        }

        return isSubscribed;
    }

    public virtual async Task<bool> UnsubscribeTopicAsync(string topic)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, topic).ConfigureAwait(false);
        return _subscriptionService.UnsubscribeTopic(Context.ConnectionId, topic, GetTopicPrefix);
    }

    protected abstract string GetTopicPrefix(string topicName);
}
