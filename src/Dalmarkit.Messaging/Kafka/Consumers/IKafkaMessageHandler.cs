using Confluent.Kafka;

namespace Dalmarkit.Messaging.Kafka.Consumers;

public interface IKafkaMessageHandler<TValue>
{
    /// <summary>
    /// Handler invoked once per record
    /// Exceptions trigger retry per HandlerErrorMaxRetries
    /// Side-effects must be idempotent across retries
    /// Throwing OperationCanceledException stops the consumer
    /// </summary>
    /// <param name="consumeResult">Message consumed from Kafka cluster</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task HandleAsync(ConsumeResult<string, TValue> consumeResult, CancellationToken cancellationToken);
}
