using Confluent.Kafka;
using System.Text.Json;

namespace Dalmarkit.Messaging.Kafka.Serialization;


/// <summary>
/// Deserializes JSON bytes from Kafka into <typeparamref name="T"/>
/// Null or empty payloads produce the default value of <typeparamref name="T"/>
/// </summary>
/// <typeparam name="T">Data type</typeparam>
/// <remarks>
/// Creates a deserializer using the supplied <paramref name="options"/>, or
/// <see cref="JsonSerializerOptions.Default"/> when null.
/// </remarks>
/// <param name="options">JSON serializer options</param>
public sealed class KafkaJsonDeserializer<T>(JsonSerializerOptions? options = null) : IDeserializer<T>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc />
    public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
    {
        return isNull || data.IsEmpty ? default! : JsonSerializer.Deserialize<T>(data, _options)!;
    }
}
