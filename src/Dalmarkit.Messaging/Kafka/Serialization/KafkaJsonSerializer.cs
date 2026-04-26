using Confluent.Kafka;
using System.Text.Json;

namespace Dalmarkit.Messaging.Kafka.Serialization;

/// <summary>
/// Serializes values to JSON bytes for Kafka
/// A null reference is serialized as a Kafka null payload (tombstone)
/// Log-compacted topics see a delete marker rather than an explicit zero-length value
/// </summary>
/// <typeparam name="T">Data type</typeparam>
/// <param name="options">JSON serializer options used for every Serialize call</param>
public sealed class KafkaJsonSerializer<T>(JsonSerializerOptions options) : ISerializer<T>
{
    private readonly JsonSerializerOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public byte[] Serialize(T data, SerializationContext context)
    {
        // Returning null is documented Confluent.Kafka tombstone signal
        // Suppression is required because ISerializer<T>.Serialize is annotated as returning byte[] but Confluent treats null specially
        return data is null ? null! : JsonSerializer.SerializeToUtf8Bytes(data, _options);
    }
}
