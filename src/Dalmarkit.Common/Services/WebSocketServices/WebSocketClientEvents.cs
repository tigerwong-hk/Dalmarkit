using Mediator;

namespace Dalmarkit.Common.Services.WebSocketServices;

public static class WebSocketClientEvents
{
    public record OnBinaryMessageReceived(byte[] Message) : INotification;
    public record OnHealthCheckCanceled() : INotification;
    public record OnHealthCheckFailure(Exception Failure) : INotification;
    public record OnMaxReconnectionAttemptsReached(int Attempts) : INotification;
    public record OnNoServerHeartbeatDisconnectFailure(Exception Failure) : INotification;
    public record OnNoServerHeartbeatReceived(long LastReceivedTimestampMilliseconds) : INotification;
    public record OnProcessReceivedMessageFailure(Exception Failure) : INotification;
    public record OnReceiveFailure(Exception Failure) : INotification;
    public record OnReceiveCanceled() : INotification;
    public record OnReconnectCanceled() : INotification;
    public record OnReconnectError(string ErrorDescription) : INotification;
    public record OnReconnectFailure(int Attempts, Exception Failure) : INotification;
    public record OnShutdownCheckHealthTaskTimeout() : INotification;
    public record OnShutdownCheckHealthTaskFailure(Exception Failure) : INotification;
    public record OnShutdownReceiveMessageTaskTimeout() : INotification;
    public record OnShutdownReceiveMessageTaskFailure(Exception Failure) : INotification;
    public record OnTextMessageReceived(string Message) : INotification;
    public record OnWebSocketConnected() : INotification;
    public record OnWebSocketDisconnected(string Status) : INotification;
}
