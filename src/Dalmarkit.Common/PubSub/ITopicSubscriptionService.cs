using System.Collections.Immutable;

namespace Dalmarkit.Common.PubSub;

public interface ITopicSubscriptionService
{
    ImmutableHashSet<string> GetSubscriberTopics(string subscriberId);
    ImmutableHashSet<string> GetTopicsByPrefix(string prefix);
    ImmutableHashSet<string> RemoveSubscriber(string subscriberId, Func<string, string>? GetTopicPrefix);
    bool SubscribeTopic(string subscriberId, string topicName, Func<string, string>? GetTopicPrefix);
    bool UnsubscribeTopic(string subscriberId, string topicName, Func<string, string>? GetTopicPrefix);
}
