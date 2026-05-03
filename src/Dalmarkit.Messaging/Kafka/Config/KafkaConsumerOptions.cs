using Confluent.Kafka;

namespace Dalmarkit.Messaging.Kafka.Config;

/// <summary>
/// Strongly-typed configuration for <see cref="Consumers.KafkaConsumerService{TValue}"/>
/// Bound from the <see cref="SectionName"/> configuration section by default
/// </summary>
public class KafkaConsumerOptions : KafkaClientOptions
{
    /// <summary>
    /// Configuration section name used by the default DI registration
    /// </summary>
    public const string SectionName = "KafkaConsumerOptions";

    /// <summary>
    /// Period (ms) between background commits of stored offsets
    /// Handlers call <c>StoreOffset</c> after success
    /// Offset commit request will only sent to the group coordinator periodically by the background thread
    /// Lower values bound the worst-case redelivery window after a crash; higher values reduce broker load
    /// 0 means commit on every poll
    /// </summary>
    public int AutoCommitIntervalMs { get; set; } = 5_000;

    /// <summary>
    /// What to do when no committed offset is found for the group ("earliest" or "latest")
    /// </summary>
    public AutoOffsetReset AutoOffsetReset { get; set; } = AutoOffsetReset.Earliest;

    public int FetchMaxBytes { get; set; } = 52_428_800;

    /// <summary>
    /// Consumer group id, required by Kafka for group-coordinated consumption
    /// </summary>
    public required string GroupId { get; set; }

    public int HandlerErrorBackoffMs { get; set; } = 5_000;

    /// <summary>
    /// Maximum number of retries after the initial handler attempt
    /// 0 = single attempt (no retries). Negative value = retry indefinitely until cancellation.
    /// </summary>
    public int HandlerErrorMaxRetries { get; set; } = 5;

    /// <summary>
    /// Heartbeat interval in milliseconds, should be lower than <see cref="SessionTimeoutMs"/> / 3
    /// </summary>
    public int HeartbeatIntervalMs { get; set; } = 3_000;

    /// <summary>
    /// Maximum amount of data per partition the broker returns in a fetch response (bytes)
    /// </summary>
    public int MaxPartitionFetchBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Maximum allowed time between calls to consume messages for high-level consumers
    /// </summary>
    public int MaxPollIntervalMs { get; set; } = 300_000;

    /// <summary>
    /// Maximum time in milliseconds a single Consume() poll will wait for a record
    /// </summary>
    public int PollTimeoutMs { get; set; } = 1_000;

    /// <summary>
    /// Initial backoff (ms) before the outer rebuild loop reconstructs the consumer after a fatal or transient error
    /// Doubled on each consecutive failure up to <see cref="RebuildBackoffMaxMs"/>
    /// Reset to this value after a successful poll
    /// </summary>
    public int RebuildBackoffMs { get; set; } = 1_000;

    /// <summary>
    /// Cap (ms) for the exponential rebuild backoff
    /// </summary>
    public int RebuildBackoffMaxMs { get; set; } = 30_000;

    /// <summary>
    /// Session timeout in milliseconds used by the group coordinator
    /// </summary>
    public int SessionTimeoutMs { get; set; } = 45_000;

    public required IReadOnlyList<string> SubscriptionTopics { get; set; }

    public override void Validate()
    {
        base.Validate();

        if (string.IsNullOrWhiteSpace(GroupId))
        {
            throw new ArgumentNullException(nameof(GroupId));
        }

        if (SubscriptionTopics == null)
        {
            throw new ArgumentNullException(nameof(SubscriptionTopics));
        }

        if (SubscriptionTopics.Count == 0)
        {
            throw new ArgumentException("must have at least one subscription topic", nameof(SubscriptionTopics));
        }

        if (SubscriptionTopics.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("all subscription topics must be non-empty/non-null", nameof(SubscriptionTopics));
        }

        if (AutoCommitIntervalMs < 0)
        {
            throw new ArgumentException("auto commit interval must be non-negative", nameof(AutoCommitIntervalMs));
        }

        if (FetchMaxBytes <= 0)
        {
            throw new ArgumentException("max fetch must be positive", nameof(FetchMaxBytes));
        }

        if (HandlerErrorBackoffMs < 0)
        {
            throw new ArgumentException("handler error backoff must be non-negative", nameof(HandlerErrorBackoffMs));
        }

        if (HeartbeatIntervalMs <= 0)
        {
            throw new ArgumentException("heartbeat interval must be positive", nameof(HeartbeatIntervalMs));
        }

        if (MaxPartitionFetchBytes <= 0)
        {
            throw new ArgumentException("max partition fetch must be positive", nameof(MaxPartitionFetchBytes));
        }

        if (PollTimeoutMs <= 0)
        {
            throw new ArgumentException("poll timeout must be positive", nameof(PollTimeoutMs));
        }

        if (RebuildBackoffMaxMs <= 0)
        {
            throw new ArgumentException("max rebuild backoff must be positive", nameof(RebuildBackoffMaxMs));
        }

        if (RebuildBackoffMs <= 0)
        {
            throw new ArgumentException("rebuild backoff must be positive", nameof(RebuildBackoffMs));
        }

        if (SessionTimeoutMs <= 0)
        {
            throw new ArgumentException("session time must be positive", nameof(SessionTimeoutMs));
        }

        if (HeartbeatIntervalMs * 3L >= SessionTimeoutMs)
        {
            throw new ArgumentException("heartbeat interval must be less than session timeout / 3", nameof(HeartbeatIntervalMs));
        }

        if (MaxPartitionFetchBytes > FetchMaxBytes)
        {
            throw new ArgumentException("max partition fetch must be less than max fetch", nameof(MaxPartitionFetchBytes));
        }

        if (MaxPollIntervalMs < SessionTimeoutMs)
        {
            throw new ArgumentException("max poll interval must not be shorter than session timeout", nameof(MaxPollIntervalMs));
        }

        if (RebuildBackoffMs > RebuildBackoffMaxMs)
        {
            throw new ArgumentException("rebuild backoff must be shorter than max rebuild backoff", nameof(RebuildBackoffMs));
        }
    }
}
