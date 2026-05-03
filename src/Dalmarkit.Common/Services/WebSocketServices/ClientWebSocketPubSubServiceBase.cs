using System.Collections.Immutable;
using Dalmarkit.Common.PubSub;
using Microsoft.Extensions.Logging;

namespace Dalmarkit.Common.Services.WebSocketServices;

public abstract class ClientWebSocketPubSubServiceBase(
    IWebSocketClient webSocketClient,
    ITopicSubscriptionService topicSubscriptionService,
    ITopicPublisherService topicPublisherService,
    ILogger<ClientWebSocketPubSubServiceBase> logger) : ClientWebSocketPubServiceBase(webSocketClient, topicPublisherService, logger), IClientWebSocketPubSubService
{
    private readonly ILogger<ClientWebSocketPubSubServiceBase> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ITopicSubscriptionService _topicSubscriptionService = topicSubscriptionService ?? throw new ArgumentNullException(nameof(topicSubscriptionService));

    private volatile int _isDisposed;

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        base.Dispose(disposing);
    }

    protected virtual bool PublishTopicsByPrefix<TDto>(string topicPrefix, string channelName, TDto publishDto, Func<ImmutableHashSet<string>, string, TDto, bool> publishTopics)
    {
        ImmutableHashSet<string> subscribedTopics = _topicSubscriptionService.GetTopicsByPrefix(topicPrefix);
        if (subscribedTopics.Count == 0)
        {
            return false;
        }

        try
        {
            return publishTopics(subscribedTopics, channelName, publishDto);
        }
        catch (Exception ex)
        {
            _logger.PublishTopicsByPrefixPublishTopicsException(topicPrefix, channelName, ex);
        }

        return false;
    }
}

public static partial class ClientWebSocketPubSubServiceBaseLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "PublishTopicsByPrefix: publish topics exception for prefix `{TopicPrefix}` and channel `{ChannelName}`")]
    public static partial void PublishTopicsByPrefixPublishTopicsException(
        this ILogger logger, string topicPrefix, string channelName, Exception exception);
}
