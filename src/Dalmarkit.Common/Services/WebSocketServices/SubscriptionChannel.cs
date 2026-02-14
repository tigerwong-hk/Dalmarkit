namespace Dalmarkit.Common.Services.WebSocketServices;

public class SubscriptionChannel
{
    public required string ChannelName { get; set; }
    public string? NotificationMessageType { get; set; }
}
