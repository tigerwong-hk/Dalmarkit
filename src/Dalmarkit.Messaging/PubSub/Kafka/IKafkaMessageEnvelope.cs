using Confluent.Kafka;

namespace Dalmarkit.Messaging.PubSub.Kafka;

/// <summary>
/// Abstracts the Kafka wire format chosen by <see cref="KafkaTopicPublisherService"/>
/// Implementations decide how a topic/method/payload tuple maps onto a Kafka message
/// e.g. raw payload value with metadata in headers, typed envelope wrapping the payload, CloudEvents, etc
/// </summary>
public interface IKafkaMessageEnvelope
{
    /// <summary>
    /// Publishes a message to <paramref name="topic"/> using the implementation's wire format
    /// </summary>
    /// <typeparam name="TPayload">Payload type</typeparam>
    /// <param name="topic">Topic</param>
    /// <param name="method">Method</param>
    /// <param name="payload">Payload</param>
    /// <param name="key">Key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<DeliveryResult<string, TPayload>> PublishAsync<TPayload>(
        string topic,
        string method,
        TPayload payload,
        string? key,
        CancellationToken cancellationToken);
}
