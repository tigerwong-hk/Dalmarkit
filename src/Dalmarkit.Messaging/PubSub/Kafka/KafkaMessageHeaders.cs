namespace Dalmarkit.Messaging.PubSub.Kafka;

/// <summary>
/// Header keys written by <see cref="RawPayloadKafkaMessageEnvelope"/>
/// Consumers route on <see cref="Method"/> and read <see cref="PublishTimestamp"/> for ordering / staleness checks
/// </summary>
public static class KafkaMessageHeaders
{
    public const string Method = "method";
    public const string PublishTimestamp = "publish-ts";
}
