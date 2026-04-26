namespace Dalmarkit.Messaging.Kafka.Config;

/// <summary>
/// Connection-level options shared across all Kafka producers and consumers
/// Bootstrap servers are intentionally excluded and must be provided via <see cref="IBootstrapServersProvider"/>
/// </summary>
public class AwsKafkaOptions
{
    public const string SectionName = "AwsKafkaOptions";

    /// <summary>
    /// ARN of the MSK cluster
    /// </summary>
    public required string ClusterArn { get; set; }

    public virtual void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ClusterArn);
    }
}
