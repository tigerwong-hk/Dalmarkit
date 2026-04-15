namespace Dalmarkit.Common.PubSub;

public static class WebSocketClientTopics
{
    public const string ConnectionState = "connstate";

    public static string GetConnectionStateTopic()
    {
        return ConnectionState;
    }
}
