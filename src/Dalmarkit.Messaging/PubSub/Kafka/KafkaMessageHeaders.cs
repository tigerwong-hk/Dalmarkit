using Dalmarkit.Messaging.Kafka.Consumers;

namespace Dalmarkit.Messaging.PubSub.Kafka;

/// <summary>
/// Header keys written by <see cref="TopicMessageKafkaMessageEnvelope"/>
/// Surfaced on the Kafka <c>Message.Headers</c> in addition to being embedded inside the JSON envelope so consumers can:
///   1. route on <see cref="Method"/>,
///   2. choose right deserializer based on <see cref="PayloadType"/>
///   3. detect staleness via <see cref="PublishTimestamp"/>
/// without deserializing the payload
/// <see cref="BusinessMessageId"/> aliases <see cref="KafkaConsumerHeaders.BusinessMessageId"/> so that producer and consumer agree on wire name
/// </summary>
public static class KafkaMessageHeaders
{
    public const string BusinessMessageId = KafkaConsumerHeaders.BusinessMessageId;
    public const string Method = "method";
    public const string PayloadType = KafkaConsumerHeaders.PayloadType;
    public const string PublishTimestamp = "publish-ts";
}
