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
    /// Must be a positive bounded value (no infinite mode)
    /// </summary>
    public int FlushTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Linger period in milliseconds before messages are batched and sent
    /// 0 means no batching, immediate send
    /// </summary>
    public double LingerMs { get; set; } = 5;

    /// <summary>
    /// Maximum number of <c>ProduceAsync</c> calls allowed to be in flight concurrently
    /// Bounds the await-queue inside <see cref="Producers.KafkaProducerService{TValue}"/> so a backed-up broker cannot pile up unbounded pending tasks
    /// Calls beyond this cap wait on a semaphore (cancellable via caller's <see cref="CancellationToken"/>) before proceeding to <c>producer.ProduceAsync</c>
    /// 0 means unbounded
    /// </summary>
    public int MaxInflightProduceAsync { get; set; } = 1_000;

    /// <summary>
    /// Total number of produce retries performed by librdkafka before surfacing an error
    /// </summary>
    public int MessageSendMaxRetries { get; set; } = int.MaxValue;

    /// <summary>
    /// Maximum time in milliseconds that a produce call will block waiting for queue space or delivery
    /// 0 means wait forever for delivery
    /// </summary>
    public int MessageTimeoutMs { get; set; } = 20_000;

    /// <summary>
    /// Delivery request timeout in milliseconds used by librdkafka
    /// Must be at least 1 ms, librdkafka's documented minimum
    /// </summary>
    public int RequestTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Maximum time in milliseconds that dispose will block flushing in-flight messages
    /// Should be configured strictly less than <c>HostOptions.ShutdownTimeout</c> so the host does not SIGKILL the process mid-flush
    /// Set to 0 to skip flush on shutdown (drops in-flight messages)
    /// </summary>
    public int ShutdownFlushTimeoutMs { get; set; } = 20_000;

    public override void Validate()
    {
        base.Validate();

        if (FlushTimeoutMs <= 0)
        {
            throw new ArgumentException("flush timeout must be positive", nameof(FlushTimeoutMs));
        }

        if (LingerMs < 0)
        {
            throw new ArgumentException("linger must be non-negative", nameof(LingerMs));
        }

        if (MaxInflightProduceAsync < 0)
        {
            throw new ArgumentException("max inflight produce must be non-negative", nameof(MaxInflightProduceAsync));
        }

        if (MessageSendMaxRetries < 0)
        {
            throw new ArgumentException("message send max retries must be non-negative", nameof(MessageSendMaxRetries));
        }
        if (MessageTimeoutMs < 0)
        {
            throw new ArgumentException("message timeout must be non-negative", nameof(MessageTimeoutMs));
        }

        if (RequestTimeoutMs <= 0)
        {
            throw new ArgumentException("request timeout must be positive", nameof(RequestTimeoutMs));
        }

        if (ShutdownFlushTimeoutMs < 0)
        {
            throw new ArgumentException("shutdown flush timeout must be non-negative", nameof(ShutdownFlushTimeoutMs));
        }

        if (MessageTimeoutMs > 0 && RequestTimeoutMs > MessageTimeoutMs)
        {
            throw new ArgumentException("request timeout must not be longer than message timeout", nameof(RequestTimeoutMs));
        }
    }
}
