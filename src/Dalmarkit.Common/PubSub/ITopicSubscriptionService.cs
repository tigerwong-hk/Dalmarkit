namespace Dalmarkit.Common.PubSub;

public interface ITopicSubscriptionService
{
    string ConnectionKeyPrefix { get; }
    string SubscriptionKey { get; }
    string TopicKeyPrefix { get; }

    Task<IReadOnlyCollection<string>> GetAllSubscribedTopics();
    Task<IReadOnlyCollection<string>> GetSubscribedTopicsByConnectionIdAsync(string connectionId);
    Task<IReadOnlyCollection<string>> GetSubscribedTopicsByPrefixAsync(string prefix);
    Task<IReadOnlyCollection<string>> GetTopicSubscribersAsync(string topic);
    Task<bool> IsTopicSubscribedAsync(string connectionId, string topic);
    Task<IReadOnlyCollection<string>> RemoveConnectionAsync(string connectionId);
    Task SubscribeTopicAsync(string connectionId, string topic);
    Task UnsubscribeTopicAsync(string connectionId, string topic);
}
