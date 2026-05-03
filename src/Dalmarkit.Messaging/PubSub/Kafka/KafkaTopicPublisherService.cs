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

    public virtual Task PublishToAllAsync<TPayload>(string topic, string method, TPayload payload, string? key = default, string? businessMessageId = default, CancellationToken cancellationToken = default)
    {
        // Kafka equivalent of SignalR's PublishToAll: send to a well-known fan-out topic.
        // Caller-supplied topic is preserved as part of the method label so subscribers can de-multiplex
        return PublishInternalAsync(PublishToAllTopic, BuildFanoutMethod(topic, method), payload, key, businessMessageId, cancellationToken);
    }

    public virtual Task PublishToTopicAsync<TPayload>(string topic, string method, TPayload payload, string? key = default, string? businessMessageId = default, CancellationToken cancellationToken = default)
    {
        return PublishInternalAsync(topic, method, payload, key, businessMessageId, cancellationToken);
    }

    private static string BuildFanoutMethod(string topic, string method)
    {
        return string.IsNullOrWhiteSpace(topic) ? method : $"{topic}:{method}";
    }

    private async Task PublishInternalAsync<TPayload>(string topic, string method, TPayload payload, string? key, string? businessMessageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            _logger.KafkaPublishInternalAsyncTopicNullError(key, businessMessageId);
            throw new ArgumentException("missing topic");
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            _logger.KafkaPublishInternalAsyncMethodNullError(topic, key, businessMessageId);
            throw new ArgumentException("missing method");
        }

        try
        {
            DeliveryResult<string, byte[]> result = await _messageEnvelope.PublishAsync(topic, method, payload, key, businessMessageId, cancellationToken).ConfigureAwait(false);
            _logger.KafkaPublishInternalAsyncProduceDebug(topic, method, key, businessMessageId, result.Offset, result.Partition, result.Status, result.Timestamp.UnixTimestampMs);
        }
        catch (OperationCanceledException)
        {
            _logger.KafkaPublishInternalAsyncProduceCanceledInfo(topic, method, key, businessMessageId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.KafkaPublishInternalAsyncProduceException(topic, method, key, businessMessageId, ex);
            throw;
        }
    }
}

public static partial class KafkaTopicPublisherServiceLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "KafkaPublishInternalAsync: topic null with key `{Key}` and business message id `{BusinessMessageId}`")]
    public static partial void KafkaPublishInternalAsyncTopicNullError(
        this ILogger logger, string? key, string? businessMessageId);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Error,
        Message = "KafkaPublishInternalAsync: method null for topic `{Topic}` with key `{Key}` and business message id `{BusinessMessageId}`")]
    public static partial void KafkaPublishInternalAsyncMethodNullError(
        this ILogger logger, string topic, string? key, string? businessMessageId);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Information,
        Message = "KafkaPublishInternalAsync: published topic `{Topic}` with method `{Method}`, key `{Key}` and business message id `{BusinessMessageId}` at offset `{Offset}`, partition `{Partition}`, status `{status}` and timestamp `{UnixTimestampMs}`")]
    public static partial void KafkaPublishInternalAsyncProduceDebug(
        this ILogger logger, string topic, string method, string? key, string? businessMessageId, Offset offset, Partition partition, PersistenceStatus status, long unixTimestampMs);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Information,
        Message = "KafkaPublishInternalAsync: produce canceled for topic `{Topic}` with method `{Method}`, key `{Key}` and business message id `{BusinessMessageId}`")]
    public static partial void KafkaPublishInternalAsyncProduceCanceledInfo(
        this ILogger logger, string topic, string method, string? key, string? businessMessageId);

    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Error,
        Message = "KafkaPublishInternalAsync: produce exception for topic `{Topic}` with method `{Method}`, key `{Key}` and business message id `{BusinessMessageId}`")]
    public static partial void KafkaPublishInternalAsyncProduceException(
        this ILogger logger, string topic, string method, string? key, string? businessMessageId, Exception exception);
}
