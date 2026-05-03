using Confluent.Kafka;

namespace Dalmarkit.Messaging.Kafka.Handlers;

public interface IOauthHandlers
{
    Action<IClient, string> GetOauthBearerTokenRefreshHandler(string region);
}
