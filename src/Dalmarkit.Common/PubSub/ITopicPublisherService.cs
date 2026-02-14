namespace Dalmarkit.Common.PubSub;

public interface ITopicPublisherService
{
    string ConnectionKeyPrefix { get; }
    string SubscriptionKey { get; }
    string TopicKeyPrefix { get; }

    Task<IReadOnlyCollection<string>> GetAllSubscribedTopics();
    Task<IReadOnlyCollection<string>> GetSubscribedTopicsByConnectionIdAsync(string connectionId);
    Task<IReadOnlyCollection<string>> GetSubscribedTopicsByPrefixAsync(string prefix);
    Task<IReadOnlyCollection<string>> GetTopicSubscribersAsync(string topic);
    Task<bool> IsTopicSubscribedAsync(string connectionId, string topic);
    Task PublishToAllAsync<T>(string topic, string method, T payload);
    Task PublishToSubscriberAsync<T>(string connectionId, string topic, string method, T payload);
    Task PublishToTopicAsync<T>(string topic, string method, T payload);
}
