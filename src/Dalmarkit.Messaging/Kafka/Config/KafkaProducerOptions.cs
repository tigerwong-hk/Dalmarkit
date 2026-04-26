using Confluent.Kafka;

namespace Dalmarkit.Messaging.Kafka.Config;

/// <summary>
/// Strongly-typed configuration for <see cref="Producers.KafkaProducerService{TValue}"/>
/// Bound from the <see cref="SectionName"/> configuration section by default
/// </summary>
public class KafkaProducerOptions : KafkaClientOptions
{
    /// <summary>
    /// Configuration section name used by the default DI registration
    /// </summary>
    public const string SectionName = "KafkaProducerOptions";

    /// <summary>
    /// Compression codec name (none, gzip, snappy, lz4, zstd)
    /// </summary>
    public CompressionType CompressionType { get; set; } = CompressionType.Zstd;

    /// <summary>
    /// Flush timeout in milliseconds
    /// </summary>
    public int FlushTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Linger period in milliseconds before messages are batched and sent
    /// </summary>
    public double LingerMs { get; set; } = 5;

    /// <summary>
    /// Total number of produce retries performed by librdkafka before surfacing an error
    /// </summary>
    public int MessageSendMaxRetries { get; set; } = int.MaxValue;

    /// <summary>
    /// Maximum time in milliseconds that a produce call will block waiting for queue space or delivery
    /// </summary>
    public int MessageTimeoutMs { get; set; } = 20_000;

    /// <summary>
    /// Delivery request timeout in milliseconds used by librdkafka
    /// </summary>
    public int RequestTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Maximum time in milliseconds that dispose will block flushing in-flight messages
    /// Should be configured strictly less than <c>HostOptions.ShutdownTimeout</c> so the host does not SIGKILL the process mid-flush
    /// </summary>
    public int ShutdownFlushTimeoutMs { get; set; } = 20_000;
}
