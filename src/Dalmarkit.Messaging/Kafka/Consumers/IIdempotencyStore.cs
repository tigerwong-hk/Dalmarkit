namespace Dalmarkit.Messaging.Kafka.Consumers;

/// <summary>
/// Library performs only a pre-handler "has this message already been processed?" check.
/// To achieve end-to-end exactly-once, the handler implementation must persist the
/// "processed" mark in the same transaction (or otherwise atomically) as its business
/// side effects, so a redelivered message is recognized as already-processed even if
/// the consumer crashed after side effects committed but before Kafka offset commit.
/// </summary>
public interface IIdempotencyStore
{
    ValueTask<bool> HasBeenProcessedAsync(MessageIdentity messageId, CancellationToken cancellationToken = default);
}
