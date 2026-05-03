namespace Dalmarkit.Common.PubSub;

public interface ITopicPublisherService
{
    Task PublishToAllAsync<TPayload>(string topic, string method, TPayload payload, string? key = default, string? businessMessageId = default, CancellationToken cancellationToken = default);
    Task PublishToTopicAsync<TPayload>(string topic, string method, TPayload payload, string? key = default, string? businessMessageId = default, CancellationToken cancellationToken = default);
}
