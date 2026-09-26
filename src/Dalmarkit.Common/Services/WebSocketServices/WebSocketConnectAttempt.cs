namespace Dalmarkit.Common.Services.WebSocketServices;

/// <summary>
/// The connection attempt an asynchronous server URL factory is called for
/// </summary>
/// <param name="AttemptNumber">0 for the first connect, then the reconnect attempt number (1, 2, ...)</param>
/// <param name="IsReconnect">True when the reconnection loop makes the attempt</param>
public sealed record WebSocketConnectAttempt(int AttemptNumber, bool IsReconnect);
