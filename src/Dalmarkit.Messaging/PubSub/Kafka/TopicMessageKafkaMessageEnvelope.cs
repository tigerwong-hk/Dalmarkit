using Confluent.Kafka;
using Dalmarkit.Common.PubSub;
using Dalmarkit.Messaging.Kafka.Producers;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dalmarkit.Messaging.PubSub.Kafka;

/// <summary>
/// Wraps the payload in a <see cref="TopicMessage{TPayload}"/> envelope (carrying topic, method, publish timestamp)
/// Then JSON-serializes the envelope and ships it via the shared byte-oriented Kafka producer
/// One Confluent producer instance covers every TPayload
/// Serialization happens here, not in librdkafka, so there is no per-T producer fan-out
/// <c>method</c>, <c>payload-type</c> and <c>publish-ts</c> are also surfaced as Kafka message headers so consumers can
/// route, choose correct deserializer or detect staleness without deserializing the JSON envelope
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
        string? businessMessageId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset publishTimestamp = DateTimeOffset.UtcNow;
        string resolvedBusinessMessageId = string.IsNullOrWhiteSpace(businessMessageId)
            ? Guid.NewGuid().ToString("D")
            : businessMessageId;

        TopicMessage<TPayload> wrapped = new()
        {
            BusinessMessageId = resolvedBusinessMessageId,
            Method = method,
            Payload = payload,
            PublishTimestamp = publishTimestamp,
            Topic = topic,
        };

        byte[] value = JsonSerializer.SerializeToUtf8Bytes(wrapped, _jsonSerializerOptions);

        Headers headers = new()
        {
            { KafkaMessageHeaders.BusinessMessageId, Encoding.UTF8.GetBytes(resolvedBusinessMessageId) },
            { KafkaMessageHeaders.Method, Encoding.UTF8.GetBytes(method) },
            { KafkaMessageHeaders.PayloadType, Encoding.UTF8.GetBytes(typeof(TPayload).FullName ?? typeof(TPayload).Name) },
            { KafkaMessageHeaders.PublishTimestamp, Encoding.UTF8.GetBytes(publishTimestamp.ToString("O", CultureInfo.InvariantCulture)) },
        };

        Message<string, byte[]> message = new()
        {
            // Null intentional: defers partitioning to librdkafka's configured partitioner instead of hashing empty string to single partition
            Key = key!,
            Value = value,
            Headers = headers,
        };

        return _producer.ProduceAsync(topic, message, cancellationToken);
    }
}
