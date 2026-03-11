namespace Dalmarkit.Common.PubSub;

public interface ITopicPublisherService
{
    Task PublishToAllAsync<T>(string topic, string method, T payload);
    Task PublishToTopicAsync<T>(string topic, string method, T payload);
}
