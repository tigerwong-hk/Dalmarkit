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
            _logger.PublishToAllTopicNullError();
            throw new ArgumentException("missing topic");
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            _logger.PublishToAllMethodNullError(topic);
            throw new ArgumentException("missing method");
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
            throw;
        }
    }

    public virtual async Task PublishToTopicAsync<TPayload>(string topic, string method, TPayload payload)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            _logger.PublishToTopicTopicNullError();
            throw new ArgumentException("missing topic");
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            _logger.PublishToTopicMethodNullError(topic);
            throw new ArgumentException("missing method");
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
            throw;
        }
    }
}

public static partial class SignalRTopicPublisherServiceLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "PublishToAll: topic null")]
    public static partial void PublishToAllTopicNullError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Error,
        Message = "PublishToAll: method null for topic `{Topic}`")]
    public static partial void PublishToAllMethodNullError(
        this ILogger logger, string topic);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Debug,
        Message = "PublishToAll: topic `{Topic}` with method `{Method}`")]
    public static partial void PublishToAllDebug(
        this ILogger logger, string topic, string method);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Error,
        Message = "PublishToAll: exception for topic `{Topic}` with method `{Method}`")]
    public static partial void PublishToAllException(
        this ILogger logger, string topic, string method, Exception exception);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Error,
        Message = "PublishToTopic: topic null")]
    public static partial void PublishToTopicTopicNullError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 2020,
        Level = LogLevel.Error,
        Message = "PublishToTopic: method null for topic `{Topic}`")]
    public static partial void PublishToTopicMethodNullError(
        this ILogger logger, string topic);

    [LoggerMessage(
        EventId = 2030,
        Level = LogLevel.Debug,
        Message = "PublishToTopic: topic `{Topic}` with method `{Method}`")]
    public static partial void PublishToTopicDebug(
        this ILogger logger, string topic, string method);

    [LoggerMessage(
        EventId = 2040,
        Level = LogLevel.Error,
        Message = "PublishToTopic: exception for topic `{Topic}` with method `{Method}`")]
    public static partial void PublishToTopicException(
        this ILogger logger, string topic, string method, Exception exception);
}
