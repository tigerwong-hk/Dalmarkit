using Microsoft.Extensions.Logging;

namespace Dalmarkit.Common.PubSub.SignalR;

public class SignalRTopicPublisherService(
    ISignalRHubPublisher hubPublisher,
    ILogger<SignalRTopicPublisherService> logger) : ITopicPublisherService
{
    private readonly ISignalRHubPublisher _hubPublisher = hubPublisher ?? throw new ArgumentNullException(nameof(hubPublisher));
    private readonly ILogger<SignalRTopicPublisherService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

    public virtual async Task PublishToTopicAsync<TPayload>(string topic, string method, TPayload payload)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            _logger.PublishToTopicTopicNull();
            return;
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            _logger.PublishToTopicMethodNull(topic);
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
        Message = "PublishToAll: topic null")]
    public static partial void PublishToAllTopicNull(
        this ILogger logger);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Error,
        Message = "PublishToAll: method null for topic `{Topic}`")]
    public static partial void PublishToAllMethodNull(
        this ILogger logger, string topic);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Debug,
        Message = "PublishToAll: topic `{Topic}` with method `{Method}`")]
    public static partial void PublishToAllDebug(
        this ILogger logger, string topic, string method);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Error,
        Message = "PublishToAll: exception for topic `{Topic}` with method `{Method}`")]
    public static partial void PublishToAllException(
        this ILogger logger, string topic, string method, Exception exception);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Error,
        Message = "PublishToTopic: topic null")]
    public static partial void PublishToTopicTopicNull(
        this ILogger logger);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Error,
        Message = "PublishToTopic: method null for topic `{Topic}`")]
    public static partial void PublishToTopicMethodNull(
        this ILogger logger, string topic);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Debug,
        Message = "PublishToTopic: topic `{Topic}` with method `{Method}`")]
    public static partial void PublishToTopicDebug(
        this ILogger logger, string topic, string method);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Error,
        Message = "PublishToTopic: subscribers empty for topic `{Topic}` with method `{Method}`")]
    public static partial void PublishToTopicException(
        this ILogger logger, string topic, string method, Exception exception);
}
