namespace Dalmarkit.Messaging.Kafka.Consumers;

/// <summary>
/// Consumes deserialized messages of type <typeparamref name="TValue"/> from Kafka
/// Execution is driven by the caller via <see cref="ConsumeAsync"/>
/// Implementation owns its own outer rebuild loop and reacts to fatal librdkafka errors transparently to the caller
/// </summary>
/// <typeparam name="TValue">Value type</typeparam>
public interface IKafkaConsumerService<TValue>
{
    /// <summary>
    /// Subscribes to topics and invokes message handler for every record until <paramref name="cancellationToken"/> fires
    /// Each successful handler invocation calls <c>StoreOffset</c>
    /// Background thread commits stored offsets periodically every <c>AutoCommitIntervalMs</c> ms
    /// Implementation commits stored offsets on partition revoke and graceful shutdown
    /// On fatal errors the implementation rebuilds the consumer with exponential backoff rather than surfacing the error to the caller
    /// </summary>
    /// <param name="cancellationToken">Cancel consuming records</param>
    Task ConsumeAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// True while a <see cref="ConsumeAsync"/> call is active (either polling or backing off between rebuilds)
    /// Returns false before the first call and after the call observes cancellation and unwinds
    /// </summary>
    bool IsRunning { get; }
}
