using Dalmarkit.Common.Validation;

namespace Dalmarkit.Common.Services.WebSocketServices;

public class WebSocketClientOptions
{
    public int ConnectionTimeoutMilliseconds { get; set; } = 10000;
    public int GracefulShutdownTimeoutMilliseconds { get; set; } = 10000;
    public HealthCheckPolicy? HealthCheck { get; set; }
    public int KeepAliveIntervalMilliseconds { get; set; }
    public int KeepAliveTimeoutMilliseconds { get; set; }
    public long MaxMessageByteSize { get; set; } = 1024 * 1024;
    public int ReceiveBufferByteSize { get; set; } = 1024 * 8;
    public ReconnectionPolicy? Reconnection { get; set; }
    public int RequestTimeoutMilliseconds { get; set; } = 10000;
    public string ServerUrl { get; set; } = string.Empty;

    public void Validate()
    {
        if (ConnectionTimeoutMilliseconds < 0)
        {
            throw new ArgumentException("Connection timeout must be non-negative", nameof(ConnectionTimeoutMilliseconds));
        }

        if (GracefulShutdownTimeoutMilliseconds < 0)
        {
            throw new ArgumentException("Graceful shutdown timeout must be non-negative", nameof(GracefulShutdownTimeoutMilliseconds));
        }

        if (HealthCheck != null)
        {
            if (HealthCheck.IntervalMilliseconds <= 0)
            {
                throw new ArgumentException("Health check interval must be positive", nameof(HealthCheck.IntervalMilliseconds));
            }

            if (HealthCheck.TimeoutMilliseconds <= 0)
            {
                throw new ArgumentException("Health check timeout must be positive", nameof(HealthCheck.TimeoutMilliseconds));
            }
        }

        if (KeepAliveIntervalMilliseconds < 0)
        {
            throw new ArgumentException("Keep alive interval must be non-negative", nameof(KeepAliveIntervalMilliseconds));
        }

        if (KeepAliveTimeoutMilliseconds < 0)
        {
            throw new ArgumentException("Keep alive timeout must be non-negative", nameof(KeepAliveTimeoutMilliseconds));
        }

        if (ReceiveBufferByteSize <= 0)
        {
            throw new ArgumentException("Receive buffer size must be positive", nameof(ReceiveBufferByteSize));
        }

        if (Reconnection?.DelayMilliseconds <= 0)
        {
            throw new ArgumentException("Reconnect delay must be positive", nameof(Reconnection.DelayMilliseconds));
        }

        if (RequestTimeoutMilliseconds < 0)
        {
            throw new ArgumentException("Request timeout must be non-negative", nameof(RequestTimeoutMilliseconds));
        }

        _ = Guard.NotNullOrWhiteSpace(ServerUrl, nameof(ServerUrl));
        Uri serverUri = new(ServerUrl);
        if (!serverUri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) && !serverUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Server URL must use ws:// or wss:// scheme", nameof(ServerUrl));
        }
    }

    public class HealthCheckPolicy
    {
        public int IntervalMilliseconds { get; set; } = 2000;
        public int TimeoutMilliseconds { get; set; } = 20000;

    }

    public class ReconnectionPolicy
    {
        public int MaxAttempts { get; set; } = 3;
        public int DelayMilliseconds { get; set; } = 5000;
    }
}
