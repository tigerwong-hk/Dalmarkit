namespace Dalmarkit.Common.PubSub.SignalR;

public static class WebSocketClientTopics
{
    public const string ConnectionState = "connstate";

    public static string GetConnectionStateTopic()
    {
        return ConnectionState;
    }
}
