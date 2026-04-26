using Confluent.Kafka;

namespace Dalmarkit.Messaging.Kafka.Producers;

/// <summary>
/// Publishes JSON-serialized messages of type <typeparamref name="TValue"/> to Kafka
/// Implementations are thread-safe and should be registered as singletons
/// </summary>
/// <typeparam name="TValue">Value type</typeparam>
public interface IKafkaProducerService<TValue> : IDisposable
{
    /// <summary>
    /// Produces <paramref name="value"/> to <paramref name="topic"/>, using <paramref name="key"/> for partitioning
    /// </summary>
    /// <param name="topic">Topic</param>
    /// <param name="key">Partition Key</param>
    /// <param name="value">Value</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<DeliveryResult<string, TValue>> ProduceAsync(
        string topic,
        string? key,
        TValue value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a fully-constructed Kafka <paramref name="message"/> (including headers) to <paramref name="topic"/>
    /// </summary>
    /// <param name="topic">Topic</param>
    /// <param name="message">Kafka message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<DeliveryResult<string, TValue>> ProduceAsync(
        string topic,
        Message<string, TValue> message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks until all in-flight messages have been delivered or the flush timeout elapses
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
