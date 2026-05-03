namespace Dalmarkit.Messaging.Kafka.Consumers;

/// <summary>
/// Well-known Kafka header names read by <see cref="KafkaConsumerService{TValue}"/>
/// Producer-side envelopes (e.g. <c>TopicMessageKafkaMessageEnvelope</c> in the application layer) must
/// write headers under these exact names so consumer-side library code can read them transport-agnostically
/// </summary>
public static class KafkaConsumerHeaders
{
    /// <summary>
    /// Logical idempotency key produced by the publishing envelope; consumed into
    /// <see cref="MessageIdentity.BusinessMessageId"/> for dedupe
    /// </summary>
    public const string BusinessMessageId = "business-message-id";

    public const string PayloadType = "payload-type";
}
