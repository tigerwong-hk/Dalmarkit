namespace Dalmarkit.Messaging.Kafka.Config;

/// <summary>
/// Common connection and AWS MSK IAM authentication settings shared by producers and consumers
/// </summary>
public abstract class KafkaClientOptions
{
    /// <summary>
    /// Optional comma-separated bootstrap server list used as a bypass for non-AWS brokers or local development
    /// When set, the client uses this value instead of calling <see cref="IBootstrapServersProvider"/>, and
    /// SASL/SCRAM credentials below are used in place of AWS MSK IAM OAuth
    /// </summary>
    public string? BootstrapServers { get; set; }

    /// <summary>
    /// AWS region of the MSK cluster (e.g. "us-east-1"); required when <see cref="BootstrapServers"/> is not set
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// SASL/SCRAM username; required when <see cref="BootstrapServers"/> is set
    /// </summary>
    public string? SaslUsername { get; set; }

    /// <summary>
    /// SASL/SCRAM password; required when <see cref="BootstrapServers"/> is set
    /// </summary>
    public string? SaslPassword { get; set; }

    /// <summary>
    /// Service identifier used to compose the Kafka client id (combined with the local hostname)
    /// </summary>
    public required string ServiceName { get; set; }

    public virtual void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ServiceName);

        if (string.IsNullOrWhiteSpace(BootstrapServers))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Region);
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(SaslUsername);
            ArgumentException.ThrowIfNullOrWhiteSpace(SaslPassword);
        }
    }
}
