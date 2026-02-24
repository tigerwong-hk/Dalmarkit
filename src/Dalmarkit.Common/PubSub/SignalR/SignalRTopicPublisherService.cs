using Microsoft.Extensions.Logging;

namespace Dalmarkit.Common.PubSub.SignalR;

public class SignalRTopicPublisherService(
    ITopicSubscriptionService subscriptionService,
    ISignalRHubPublisher hubPublisher,
    ILogger<SignalRTopicPublisherService> logger) : ITopicPublisherService
{
    public virtual string ConnectionKeyPrefix => _subscriptionService.ConnectionKeyPrefix;
    public virtual string SubscriptionKey => _subscriptionService.SubscriptionKey;
    public virtual string TopicKeyPrefix => _subscriptionService.TopicKeyPrefix;

    private readonly ISignalRHubPublisher _hubPublisher = hubPublisher ?? throw new ArgumentNullException(nameof(hubPublisher));
    private readonly ILogger<SignalRTopicPublisherService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ITopicSubscriptionService _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));

    public virtual async Task<IReadOnlyCollection<string>> GetAllSubscribedTopics()
    {
        return await _subscriptionService.GetAllSubscribedTopics();
    }

    public virtual async Task<IReadOnlyCollection<string>> GetSubscribedTopicsByConnectionIdAsync(string connectionId)
    {
        return await _subscriptionService.GetSubscribedTopicsByConnectionIdAsync(connectionId);
    }

    public virtual async Task<IReadOnlyCollection<string>> GetSubscribedTopicsByPrefixAsync(string prefix)
    {
        return await _subscriptionService.GetSubscribedTopicsByPrefixAsync(prefix);
    }

    public virtual async Task<IReadOnlyCollection<string>> GetTopicSubscribersAsync(string topic)
    {
        return await _subscriptionService.GetTopicSubscribersAsync(topic);
    }

    public virtual async Task<bool> IsTopicSubscribedAsync(string connectionId, string topic)
    {
        return await _subscriptionService.IsTopicSubscribedAsync(connectionId, topic);
    }

    public virtual async Task PublishToAllAsync<TPayload>(string topic, string method, TPayload payload)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            _logger.PublishToAllTopicNull();
            return;
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            _logger.PublishToAllMethodNull(topic);
            return;
        }

        TopicMessage<TPayload> message = new()
        {
            Topic = topic,
            Method = method,
            Payload = payload,
            PublishTimestamp = DateTimeOffset.UtcNow
        };

        try
        {
            await _hubPublisher.PublishToAll(message);
            _logger.PublishToAllDebug(topic, method);
        }
        catch (Exception ex)
        {
            _logger.PublishToAllException(topic, method, ex);
        }
    }

    public virtual async Task PublishToSubscriberAsync<TPayload>(
        string connectionId, string topic, string method, TPayload payload)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            _logger.PublishToSubscriberConnectionIdNull();
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            _logger.PublishToSubscriberTopicNull(connectionId);
            return;
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            _logger.PublishToSubscriberMethodNull(topic, connectionId);
            return;
        }

        if (!await _subscriptionService.IsTopicSubscribedAsync(connectionId, topic))
        {
            _logger.PublishToSubscriberNotSubscribed(topic, method, connectionId);
            return;
        }

        TopicMessage<TPayload> message = new()
        {
            Topic = topic,
            Method = method,
            Payload = payload,
            PublishTimestamp = DateTimeOffset.UtcNow
        };

        try
        {
            await _hubPublisher.PublishToConnection(connectionId, message);
            _logger.PublishToSubscriberDebug(topic, method, connectionId);
        }
        catch (Exception ex)
        {
            _logger.PublishToSubscriberException(topic, method, connectionId, ex);
        }
    }

    public virtual async Task PublishToTopicAsync<TPayload>(string topic, string method, TPayload payload)
    {
        TopicMessage<TPayload> message = new()
        {
            Topic = topic,
            Method = method,
            Payload = payload,
            PublishTimestamp = DateTimeOffset.UtcNow
        };

        try
        {
            await _hubPublisher.PublishToGroup(topic, message);
            _logger.PublishToTopicDebug(topic, method);
        }
        catch (Exception ex)
        {
            _logger.PublishToTopicException(topic, method, ex);
        }
    }
}

public static partial class SignalRTopicPublisherServiceLogs
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Error,
        Message = "Publish to all topic null")]
    public static partial void PublishToAllTopicNull(
        this ILogger logger);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Error,
        Message = "Publish to all method null for topic `{Topic}`")]
    public static partial void PublishToAllMethodNull(
        this ILogger logger, string topic);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Debug,
        Message = "Publish to all for topic/method `{Topic}`/`{Method}`")]
    public static partial void PublishToAllDebug(
        this ILogger logger, string topic, string method);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Error,
        Message = "Publish to all exception for topic/method `{Topic}`/`{Method}`")]
    public static partial void PublishToAllException(
        this ILogger logger, string topic, string method, Exception exception);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Error,
        Message = "Publish to subscriber connection ID null")]
    public static partial void PublishToSubscriberConnectionIdNull(
        this ILogger logger);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Error,
        Message = "Publish to subscriber topic null for connection ID {ConnectionId}")]
    public static partial void PublishToSubscriberTopicNull(
        this ILogger logger, string connectionId);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Error,
        Message = "Publish to subscriber method null for topic `{Topic}` with connection ID {ConnectionID}")]
    public static partial void PublishToSubscriberMethodNull(
        this ILogger logger, string topic, string connectionId);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Error,
        Message = "Publish to subscriber but subscriber not subscribed for topic/method `{Topic}`/`{Method}`: {ConnectionId}")]
    public static partial void PublishToSubscriberNotSubscribed(
        this ILogger logger, string topic, string method, string connectionId);

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Debug,
        Message = "Publish to subscriber for topic/method `{Topic}`/`{Method}`: {ConnectionId}")]
    public static partial void PublishToSubscriberDebug(
        this ILogger logger, string topic, string method, string connectionId);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Error,
        Message = "Publish to subscriber exception for topic/method `{Topic}`/`{Method}`: {ConnectionId}")]
    public static partial void PublishToSubscriberException(
        this ILogger logger, string topic, string method, string connectionId, Exception exception);

    [LoggerMessage(
        EventId = 110,
        Level = LogLevel.Debug,
        Message = "Publish to topic for topic/method `{Topic}`/`{Method}`")]
    public static partial void PublishToTopicDebug(
        this ILogger logger, string topic, string method);

    [LoggerMessage(
        EventId = 120,
        Level = LogLevel.Error,
        Message = "Publish to topic subscribers empty for topic/method `{Topic}`/`{Method}`")]
    public static partial void PublishToTopicException(
        this ILogger logger, string topic, string method, Exception exception);
}
