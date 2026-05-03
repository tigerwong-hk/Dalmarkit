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
    public int ReceiveBinaryChannelCapacity { get; set; } = 8192;
    public int ReceiveTextChannelCapacity { get; set; } = 8192;
    public ReconnectionPolicy? Reconnection { get; set; }
    public int RequestTimeoutMilliseconds { get; set; } = 10000;
    public int ResponseTimeoutMilliseconds { get; set; } = 30000;
    public required string ServerUrl { get; set; }
    public string? WebSocketName { get; set; }

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

        if (MaxMessageByteSize < 0)
        {
            throw new ArgumentException("Maximum message size must be non-negative", nameof(MaxMessageByteSize));
        }

        if (KeepAliveTimeoutMilliseconds < 0)
        {
            throw new ArgumentException("Keep alive timeout must be non-negative", nameof(KeepAliveTimeoutMilliseconds));
        }

        if (ReceiveBufferByteSize <= 0)
        {
            throw new ArgumentException("Receive buffer size must be positive", nameof(ReceiveBufferByteSize));
        }

        if (ReceiveBinaryChannelCapacity <= 0)
        {
            throw new ArgumentException("Receive binary channel capacity must be positive", nameof(ReceiveBinaryChannelCapacity));
        }

        if (ReceiveTextChannelCapacity <= 0)
        {
            throw new ArgumentException("Receive text channel capacity must be positive", nameof(ReceiveTextChannelCapacity));
        }

        if (Reconnection?.DelayMilliseconds <= 0)
        {
            throw new ArgumentException("Reconnect delay must be positive", nameof(Reconnection.DelayMilliseconds));
        }

        if (RequestTimeoutMilliseconds < 0)
        {
            throw new ArgumentException("Request timeout must be non-negative", nameof(RequestTimeoutMilliseconds));
        }

        if (ResponseTimeoutMilliseconds < 0)
        {
            throw new ArgumentException("Response timeout must be non-negative", nameof(ResponseTimeoutMilliseconds));
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
