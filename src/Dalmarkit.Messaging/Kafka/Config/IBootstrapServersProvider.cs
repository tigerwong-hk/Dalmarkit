namespace Dalmarkit.Messaging.Kafka.Config;

/// <summary>
/// Provides AWS MSK bootstrap server addresses from a source external to appsettings configuration
/// (e.g., AWS SDK, AWS Secrets Manager, AWS SSM Parameter Store)
/// </summary>
public interface IBootstrapServersProvider
{
    /// <summary>
    /// Returns the bootstrap server list. Implementations are expected to cache the result
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<string> GetBootstrapServersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates any cached bootstrap server list so that next <see cref="GetBootstrapServersAsync(CancellationToken)"/> call re-fetches from upstream source
    /// Called when the producer rebuilds after a fatal error so a stale broker list (e.g. after MSK scaling or AZ failover) does not get reused
    /// </summary>
    void InvalidateCachedBootstrapServers();
}
