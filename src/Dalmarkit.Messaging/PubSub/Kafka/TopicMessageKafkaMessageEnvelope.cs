using Confluent.Kafka;
using Dalmarkit.Common.PubSub;
using Dalmarkit.Messaging.Kafka.Producers;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Dalmarkit.Messaging.PubSub.Kafka;

/// <summary>
/// Wraps the payload in a <see cref="TopicMessage{TPayload}"/> envelope (carrying topic, method, publish timestamp)
/// Then JSON-serializes the envelope and ships it via the shared byte-oriented Kafka producer
/// One Confluent producer instance covers every TPayload
/// Serialization happens here, not in librdkafka, so there is no per-T producer fan-out
/// </summary>
public sealed class TopicMessageKafkaMessageEnvelope : IKafkaMessageEnvelope
{
    /// <summary>
    /// DI key used to look up the <see cref="JsonSerializerOptions"/> applied to the envelope
    /// Register a keyed singleton under this key from the host so Kafka payload serialization can be
    /// configured (e.g. decimal/enum converters) consistently with other transports
    /// </summary>
    public const string JsonSerializerOptionsServiceKey = "Dalmarkit.Sample.Application.PubSub.Kafka";

    private readonly IKafkaProducerService<byte[]> _producer;
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public TopicMessageKafkaMessageEnvelope(
        IKafkaProducerService<byte[]> producer,
        [FromKeyedServices(JsonSerializerOptionsServiceKey)] JsonSerializerOptions jsonSerializerOptions)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(jsonSerializerOptions);

        _producer = producer;
        _jsonSerializerOptions = jsonSerializerOptions;
    }

    public Task<DeliveryResult<string, byte[]>> PublishAsync<TPayload>(
        string topic,
        string method,
        TPayload payload,
        string? key,
        CancellationToken cancellationToken)
    {
        TopicMessage<TPayload> wrapped = new()
        {
            Topic = topic,
            Method = method,
            Payload = payload,
            PublishTimestamp = DateTimeOffset.UtcNow,
        };

        byte[] value = JsonSerializer.SerializeToUtf8Bytes(wrapped, _jsonSerializerOptions);

        return _producer.ProduceAsync(topic, key, value, cancellationToken);
    }
}
