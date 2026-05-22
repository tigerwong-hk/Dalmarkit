namespace Dalmarkit.Common.PubSub;

public static class WebSocketClientTopics
{
    public const string TopicDelimiter = "-";

    public const string AuthenticationState = "authnstate";
    public const string ConnectionState = "connstate";

    public static string GetAuthenticationStateTopic(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? AuthenticationState : AuthenticationState + TopicDelimiter + name;
    }

    public static string GetConnectionStateTopic(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? ConnectionState : ConnectionState + TopicDelimiter + name;
    }
}
