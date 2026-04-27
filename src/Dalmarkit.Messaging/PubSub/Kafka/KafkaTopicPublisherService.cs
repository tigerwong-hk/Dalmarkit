using Confluent.Kafka;
using Dalmarkit.Common.PubSub;
using Microsoft.Extensions.Logging;

namespace Dalmarkit.Messaging.PubSub.Kafka;

public class KafkaTopicPublisherService(
    IKafkaMessageEnvelope messageEnvelope,
    ILogger<KafkaTopicPublisherService> logger) : ITopicPublisherService
{
    public const string PublishToAllTopic = "*";

    private readonly IKafkaMessageEnvelope _messageEnvelope = messageEnvelope ?? throw new ArgumentNullException(nameof(messageEnvelope));
    private readonly ILogger<KafkaTopicPublisherService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public virtual Task PublishToAllAsync<TPayload>(string topic, string method, TPayload payload, string? key = default, CancellationToken cancellationToken = default)
    {
        // Kafka equivalent of SignalR's PublishToAll: send to a well-known fan-out topic.
        // Caller-supplied topic is preserved as part of the method label so subscribers can de-multiplex
        return PublishInternalAsync(PublishToAllTopic, BuildFanoutMethod(topic, method), payload, key, cancellationToken);
    }

    public virtual Task PublishToTopicAsync<TPayload>(string topic, string method, TPayload payload, string? key = default, CancellationToken cancellationToken = default)
    {
        return PublishInternalAsync(topic, method, payload, key, cancellationToken);
    }

    private static string BuildFanoutMethod(string topic, string method)
    {
        return string.IsNullOrWhiteSpace(topic) ? method : $"{topic}:{method}";
    }

    private async Task PublishInternalAsync<TPayload>(string topic, string method, TPayload payload, string? key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            _logger.KafkaPublishInternalAsyncTopicNullError();
            throw new ArgumentException("missing topic");
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            _logger.KafkaPublishInternalAsyncMethodNullError(topic);
            throw new ArgumentException("missing method");
        }

        try
        {
            DeliveryResult<string, byte[]> result = await _messageEnvelope.PublishAsync(topic, method, payload, key, cancellationToken).ConfigureAwait(false);
            _logger.KafkaPublishInternalAsyncProduceDebug(topic, method, key, result.Offset, result.Partition, result.Status, result.Timestamp.UnixTimestampMs);
        }
        catch (OperationCanceledException)
        {
            _logger.KafkaPublishInternalAsyncProduceCanceledInfo(topic, method);
            throw;
        }
        catch (Exception ex)
        {
            _logger.KafkaPublishInternalAsyncProduceException(topic, method, ex);
            throw;
        }
    }
}

public static partial class KafkaTopicPublisherServiceLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "KafkaPublishInternalAsync: topic null")]
    public static partial void KafkaPublishInternalAsyncTopicNullError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Error,
        Message = "KafkaPublishInternalAsync: method null for topic `{Topic}`")]
    public static partial void KafkaPublishInternalAsyncMethodNullError(
        this ILogger logger, string topic);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Information,
        Message = "KafkaPublishInternalAsync: published topic `{Topic}` with method `{Method}` and key `{Key}` at offset `{Offset}`, partition `{Partition}`, status `{status}` and timestamp `{UnixTimestampMs}`")]
    public static partial void KafkaPublishInternalAsyncProduceDebug(
        this ILogger logger, string topic, string method, string? key, Offset offset, Partition partition, PersistenceStatus status, long unixTimestampMs);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Information,
        Message = "KafkaPublishInternalAsync: produce canceled for topic `{Topic}` with method `{Method}`")]
    public static partial void KafkaPublishInternalAsyncProduceCanceledInfo(
        this ILogger logger, string topic, string method);

    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Error,
        Message = "KafkaPublishInternalAsync: produce exception for topic `{Topic}` with method `{Method}`")]
    public static partial void KafkaPublishInternalAsyncProduceException(
        this ILogger logger, string topic, string method, Exception exception);
}
