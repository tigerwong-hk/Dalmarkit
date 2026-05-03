using Confluent.Kafka;
using Dalmarkit.Messaging.Kafka.Config;
using Dalmarkit.Messaging.Kafka.Handlers;
using Dalmarkit.Messaging.Kafka.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Dalmarkit.Messaging.Kafka.Consumers;

/// <summary>
/// <para>
/// Confluent.Kafka-based consumer supporting AWS MSK IAM (OAuthBearer) and SASL/SCRAM
/// Keys are deserialized as UTF-8 strings
/// Values are deserialized with the built-in <see cref="Deserializers.ByteArray"/> when <typeparamref name="TValue"/> is <see cref="byte"/>[],
/// <see cref="Deserializers.Utf8"/> when <see cref="string"/>, or <see cref="KafkaJsonDeserializer{T}"/> otherwise
/// </para>
/// <para>
/// Offset commit policy:
/// Each successful handler invocation calls <c>StoreOffset</c>
/// Background thread commits stored offsets periodically every <c>AutoCommitIntervalMs</c> ms
/// Implementation commits stored offsets on partition revoke and graceful shutdown
/// </para>
/// <para>
/// Failure handling:
/// - Handler exceptions are retried with backoff up to <c>HandlerErrorMaxRetries</c>, on exhaustion
///   the message is logged and skipped (the next successful StoreOffset advances past it)
/// - Fatal librdkafka errors (e.g. OAuth refresh failure that exhausts retries) are surfaced to the error handler, which flips an internal flag;
///   the inner poll loop exits, the outer rebuild loop reconstructs the consumer with exponential backoff (<c>RebuildBackoffMs</c> doubling up to <c>RebuildBackoffMaxMs</c>) and resubscribes
/// </para>
/// </summary>
/// <typeparam name="TValue">Value type</typeparam>
public class KafkaConsumerService<TValue> : IKafkaConsumerService<TValue>
{
    /// <summary>
    /// DI key used to look up the <see cref="JsonSerializerOptions"/> applied by <see cref="KafkaJsonDeserializer{T}"/>
    /// when <typeparamref name="TValue"/> is neither <see cref="byte"/>[] nor <see cref="string"/>
    /// Register a keyed singleton under this key from the host so consumer-side JSON deserialization mirrors the
    /// converters (e.g. decimal/enum) used by the producer-side envelope; consumers registered as
    /// <see cref="byte"/>[] (the default) ignore this key and let the handler do its own deserialization
    /// </summary>
#pragma warning disable RCS1158 // Static member in generic type should use a type parameter
    public const string JsonSerializerOptionsServiceKey = "Dalmarkit.Messaging.Kafka.Consumers";
#pragma warning restore RCS1158 // Static member in generic type should use a type parameter

    private const int RebuildBackoffMsMin = 100;

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _consumeCts) != null;

    private readonly IBootstrapServersProvider _bootstrapServersProvider;
    private readonly IKafkaMessageHandler<TValue> _messageHandler;
    private readonly JsonSerializerOptions? _jsonSerializerOptions;
    private readonly KafkaConsumerOptions _options;
    private readonly ILogger<KafkaConsumerService<TValue>> _logger;
    private readonly IOauthHandlers? _oauthHandlers;

    private readonly IIdempotencyStore? _idempotencyStore;

    private CancellationTokenSource? _consumeCts;
    private volatile int _fatalError;

    /// <summary>
    /// Creates a new consumer service
    /// The underlying <see cref="IConsumer{TKey, TValue}"/> is constructed lazily on the first
    /// <see cref="ConsumeAsync(CancellationToken)"/>
    /// call and disposed at the end of that call
    /// </summary>
    /// <param name="bootstrapServersProvider">Retrieve list of Kafka bootstrap servers</param>
    /// <param name="messageHandler">Kafka message handler</param>
    /// <param name="options">Kafka consumer options</param>
    /// <param name="logger">Logger</param>
    /// <param name="oauthHandlers">OAuth handlers</param>
    /// <param name="jsonSerializerOptions">JSON serializer options used by <see cref="KafkaJsonDeserializer{T}"/> when <typeparamref name="TValue"/> is not <see cref="byte"/>[] or <see cref="string"/>
    /// (resolve from DI under <see cref="JsonSerializerOptionsServiceKey"/> so converters mirror the producer envelope)</param>
    /// <param name="idempotencyStore">Idempotency store</param>
    public KafkaConsumerService(
        IBootstrapServersProvider bootstrapServersProvider,
        IKafkaMessageHandler<TValue> messageHandler,
        IOptions<KafkaConsumerOptions> options,
        ILogger<KafkaConsumerService<TValue>> logger,
        IOauthHandlers? oauthHandlers = null,
        JsonSerializerOptions? jsonSerializerOptions = null,
        IIdempotencyStore? idempotencyStore = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrapServersProvider);
        ArgumentNullException.ThrowIfNull(messageHandler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _bootstrapServersProvider = bootstrapServersProvider;
        _messageHandler = messageHandler;
        _logger = logger;

        _jsonSerializerOptions = jsonSerializerOptions;
        _idempotencyStore = idempotencyStore;

        _options = options.Value;
        _options.Validate();

        if (string.IsNullOrWhiteSpace(_options.BootstrapServers))
        {
            ArgumentNullException.ThrowIfNull(oauthHandlers);
            _oauthHandlers = oauthHandlers;
        }
    }

    /// <inheritdoc />
    public async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource consumeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Interlocked.CompareExchange(ref _consumeCts, consumeCts, null) != null)
        {
            _logger.KafkaConsumeAsyncConsumeAlreadyInvokedInfo(_options.GroupId, _options.SubscriptionTopics);

            consumeCts.Dispose();
            throw new InvalidOperationException("consume already invoked");
        }

        int initialRebuildBackoffMs = Math.Max(RebuildBackoffMsMin, _options.RebuildBackoffMs);
        int rebuildBackoffMaxMs = Math.Max(initialRebuildBackoffMs, _options.RebuildBackoffMaxMs);
        int rebuildBackoffMs = initialRebuildBackoffMs;
        bool isRebuild = false;

        try
        {
            while (!consumeCts.IsCancellationRequested)
            {
                _ = Interlocked.Exchange(ref _fatalError, 0);

                if (isRebuild)
                {
                    InvalidateCachedBootstrapServers();
                }

                long runStart = Stopwatch.GetTimestamp();
                try
                {
                    await ConsumeInternalAsync(_options.SubscriptionTopics, consumeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.KafkaConsumeAsyncConsumeInternalCanceledInfo(_options.GroupId, _options.SubscriptionTopics);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.KafkaConsumeAsyncConsumeInternalException(_options.GroupId, _options.SubscriptionTopics, ex);
                }

                TimeSpan runDuration = Stopwatch.GetElapsedTime(runStart);
                isRebuild = true;

                // Decay the backoff if RunOnceAsync ran long enough to be considered stable; otherwise the
                // long-running consumer would stay pinned at RebuildBackoffMaxMs forever after one outage
                if (runDuration >= TimeSpan.FromMilliseconds(rebuildBackoffMaxMs) && rebuildBackoffMs != initialRebuildBackoffMs)
                {
                    _logger.KafkaConsumeAsyncRebuildBackoffResetInfo(_options.GroupId, _options.SubscriptionTopics, (int)runDuration.TotalMilliseconds, initialRebuildBackoffMs);
                    rebuildBackoffMs = initialRebuildBackoffMs;
                }

                if (consumeCts.IsCancellationRequested)
                {
                    _logger.KafkaConsumeAsyncCancellationRequestedInfo(_options.GroupId, _options.SubscriptionTopics);
                    break;
                }

                _logger.KafkaConsumeAsyncRebuildBackoffInfo(_options.GroupId, _options.SubscriptionTopics, rebuildBackoffMs);

                try
                {
                    await Task.Delay(rebuildBackoffMs, consumeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.KafkaConsumeAsyncTaskDelayCanceledInfo(_options.GroupId, _options.SubscriptionTopics);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.KafkaConsumeAsyncTaskDelayException(_options.GroupId, _options.SubscriptionTopics, ex);
                    throw;
                }

                rebuildBackoffMs = (int)Math.Min((long)rebuildBackoffMs * 2, rebuildBackoffMaxMs);
            }

            _logger.KafkaConsumeAsyncCancellationRequestedExitInfo(_options.GroupId, _options.SubscriptionTopics);
        }
        finally
        {
            CancellationTokenSource? currentCts = Interlocked.Exchange(ref _consumeCts, null);
            currentCts?.Dispose();
        }
    }

    protected static bool IsDeserializationError(ErrorCode code)
    {
        return code is ErrorCode.Local_ValueDeserialization or ErrorCode.Local_KeyDeserialization;
    }

    protected virtual string BuildClientId(string serviceName)
    {
        string hostname = Dns.GetHostName();
        return $"{serviceName}-{hostname}";
    }

    protected virtual async Task<IConsumer<string, TValue>> BuildConsumer(string groupId, CancellationToken cancellationToken = default)
    {
        ConsumerConfig consumerConfig = await BuildConsumerConfig(groupId, _options, cancellationToken).ConfigureAwait(false);

        ConsumerBuilder<string, TValue> builder = new ConsumerBuilder<string, TValue>(consumerConfig)
            .SetKeyDeserializer(Deserializers.Utf8)
            .SetValueDeserializer(GetValueDeserializer())
            .SetErrorHandler(HandleConsumerError)
            // https://github.com/confluentinc/confluent-kafka-dotnet/issues/1834 - SetLogHandler crashes librdkafka in some configs
            // .SetLogHandler((_, logMessage) => _logger.KafkaConsumerServiceConsumerDebug(_options.GroupId, logMessage.Facility, logMessage.Message))
            .SetPartitionsAssignedHandler((_, partitions) => _logger.KafkaConsumerServiceConsumerPartitionsAssignedDebug(_options.GroupId, partitions.Count))
            .SetPartitionsRevokedHandler(HandlePartitionsRevoked)
            .SetPartitionsLostHandler((_, partitions) => _logger.KafkaConsumerServiceConsumerPartitionsLostDebug(_options.GroupId, partitions));

        // Only attach the AWS MSK OAuth refresh handler when the consumer is actually configured for OAuthBearer.
        // Local-dev / non-AWS clusters use SASL/SCRAM and must not run the AWS token refresh on every reconnect.
        if (consumerConfig.SaslMechanism == SaslMechanism.OAuthBearer)
        {
            // _oauthHandlers set when BootstrapServers is empty, same condition that selects OAuthBearer at config time
            _ = builder.SetOAuthBearerTokenRefreshHandler(_oauthHandlers!.GetOauthBearerTokenRefreshHandler(_options.Region!));
        }

        return builder.Build();
    }

    protected virtual async Task<ConsumerConfig> BuildConsumerConfig(string groupId, KafkaConsumerOptions options, CancellationToken cancellationToken = default)
    {
        bool hasBootstrapServersConfig = !string.IsNullOrWhiteSpace(options.BootstrapServers);

        string bootstrapServers = hasBootstrapServersConfig
            ? options.BootstrapServers!
            : await _bootstrapServersProvider.GetBootstrapServersAsync(cancellationToken).ConfigureAwait(false);

        ConsumerConfig consumerConfig = new()
        {
            SaslMechanism = hasBootstrapServersConfig ? SaslMechanism.ScramSha512 : SaslMechanism.OAuthBearer,
            ClientId = BuildClientId(options.ServiceName),
            BootstrapServers = bootstrapServers,
            SecurityProtocol = SecurityProtocol.SaslSsl,
            AutoOffsetReset = options.AutoOffsetReset,
            GroupId = groupId,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            SessionTimeoutMs = options.SessionTimeoutMs,
            HeartbeatIntervalMs = options.HeartbeatIntervalMs,
            MaxPollIntervalMs = options.MaxPollIntervalMs,
            EnableAutoCommit = true,
            AutoCommitIntervalMs = options.AutoCommitIntervalMs,
            EnableAutoOffsetStore = false,
            MaxPartitionFetchBytes = options.MaxPartitionFetchBytes,
            FetchMaxBytes = options.FetchMaxBytes,
            EnablePartitionEof = false,
        };

        if (hasBootstrapServersConfig)
        {
            consumerConfig.SaslUsername = options.SaslUsername;
            consumerConfig.SaslPassword = options.SaslPassword;
        }

        return consumerConfig;
    }

    protected virtual void CommitAllOffsets(IConsumer<string, TValue> consumer)
    {
        try
        {
            _ = consumer.Commit();
        }
        catch (TopicPartitionOffsetException ex)
        {
            if (ex.Error.IsFatal)
            {
                _logger.KafkaCommitAllOffsetsFatalTopicPartitionOffsetException(_options.GroupId, ex);
            }
            else
            {
                _logger.KafkaCommitAllOffsetsNonFatalTopicPartitionOffsetException(_options.GroupId, ex);
            }
        }
        catch (KafkaException ex)
        {
            if (ex.Error.IsFatal)
            {
                _logger.KafkaCommitAllOffsetsFatalKafkaException(_options.GroupId, ex);
            }
            else
            {
                _logger.KafkaCommitAllOffsetsNonFatalKafkaException(_options.GroupId, ex);
            }
        }
        catch (Exception ex)
        {
            _logger.KafkaCommitAllOffsetsException(_options.GroupId, ex);
        }
    }

    protected virtual async Task ConsumeInternalAsync(
        IReadOnlyList<string> topics,
        CancellationToken cancellationToken)
    {
        using IConsumer<string, TValue> consumer = await BuildConsumer(_options.GroupId, cancellationToken).ConfigureAwait(false);
        consumer.Subscribe(topics);

        _logger.KafkaConsumeInternalAsyncSubscribeInfo(_options.GroupId, topics);

        TimeSpan pollTimeout = TimeSpan.FromMilliseconds(_options.PollTimeoutMs);

        try
        {
            while (!cancellationToken.IsCancellationRequested && _fatalError == 0)
            {
                ConsumeResult<string, TValue>? consumeResult;
                try
                {
                    consumeResult = consumer.Consume(pollTimeout);
                }
                catch (ConsumeException ex)
                {
                    if (ex.Error.IsFatal)
                    {
                        _logger.KafkaConsumeInternalAsyncConsumeFatalConsumeException(_options.GroupId, topics, ex);
                        return;
                    }

                    if (IsDeserializationError(ex.Error.Code) && ex.ConsumerRecord != null)
                    {
                        HandleDeserializationFailure(consumer, ex, cancellationToken);
                        continue;
                    }

                    _logger.KafkaConsumeInternalAsyncConsumeNonFatalConsumeException(_options.GroupId, topics, ex);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.KafkaConsumeInternalAsyncConsumeException(_options.GroupId, topics, ex);
                    continue;
                }

                if (consumeResult == null)
                {
                    _logger.KafkaConsumeInternalAsyncNullConsumeResultDebug(_options.GroupId, topics);
                    continue;
                }

                await ProcessMessageAsync(consumer, consumeResult, cancellationToken).ConfigureAwait(false);
            }

            if (_fatalError != 0)
            {
                _logger.KafkaConsumeInternalAsyncFatalErrorExitInfo(_options.GroupId, topics);
            }
            else
            {
                _logger.KafkaConsumeInternalAsyncCancellationRequestedExitInfo(_options.GroupId, topics);
            }
        }
        finally
        {
            try
            {
                _logger.KafkaConsumeInternalAsyncClosingInfo(_options.GroupId, topics);
                consumer.Close();
                _logger.KafkaConsumeInternalAsyncClosedInfo(_options.GroupId, topics);
            }
            catch (KafkaException ex)
            {
                if (ex.Error.IsFatal)
                {
                    _logger.KafkaConsumeInternalAsyncCloseFatalKafkaException(_options.GroupId, topics, ex);
                }
                else
                {
                    _logger.KafkaConsumeInternalAsyncCloseNonFatalKafkaException(_options.GroupId, topics, ex);
                }
            }
            catch (Exception ex)
            {
                _logger.KafkaConsumeInternalAsyncCloseException(_options.GroupId, topics, ex);
            }
        }
    }

    protected virtual void HandleDeserializationFailure(
        IConsumer<string, TValue> consumer,
        ConsumeException consumeException,
        CancellationToken cancellationToken)
    {
        ConsumeResult<byte[], byte[]> consumeRecord = consumeException.ConsumerRecord;

        _logger.KafkaHandleDeserializationFailureSkippingDeserializationFailureWarning(
            _options.GroupId,
            consumeRecord.Topic,
            consumeRecord.Partition.Value,
            consumeRecord.Offset.Value,
            consumeException.Error.Code,
            consumeException);

        // Advance past poison record to keep partition moving, data loss is logged
        try
        {
            consumer.StoreOffset(new TopicPartitionOffset(consumeRecord.TopicPartition, consumeRecord.Offset + 1));
        }
        catch (TopicPartitionOffsetException ex)
        {
            if (ex.Error.IsFatal)
            {
                _logger.KafkaHandleDeserializationFailureFatalTopicPartitionOffsetException(_options.GroupId, consumeRecord.Topic, consumeRecord.Partition.Value, consumeRecord.Offset.Value, ex);
            }
            else
            {
                _logger.KafkaHandleDeserializationFailureNonFatalTopicPartitionOffsetException(_options.GroupId, consumeRecord.Topic, consumeRecord.Partition.Value, consumeRecord.Offset.Value, ex);
            }
        }
        catch (KafkaException ex)
        {
            if (ex.Error.IsFatal)
            {
                _logger.KafkaHandleDeserializationFailureFatalKafkaException(_options.GroupId, consumeRecord.Topic, consumeRecord.Partition.Value, consumeRecord.Offset.Value, ex);
            }
            else
            {
                _logger.KafkaHandleDeserializationFailureNonFatalKafkaException(_options.GroupId, consumeRecord.Topic, consumeRecord.Partition.Value, consumeRecord.Offset.Value, ex);
            }
        }
        catch (Exception ex)
        {
            _logger.KafkaHandleDeserializationFailureException(_options.GroupId, consumeRecord.Topic, consumeRecord.Partition.Value, consumeRecord.Offset.Value, ex);
        }
    }

    protected virtual void InvalidateCachedBootstrapServers()
    {
        try
        {
            _bootstrapServersProvider.InvalidateCachedBootstrapServers();
        }
        catch (Exception ex)
        {
            _logger.KafkaInvalidateCachedBootstrapServersException(_options.GroupId, ex);
        }
    }

    protected virtual async Task ProcessMessageAsync(
        IConsumer<string, TValue> consumer,
        ConsumeResult<string, TValue> consumeResult,
        CancellationToken cancellationToken)
    {
        string? businessMessageId = ReadBusinessMessageIdHeader(consumeResult);
        if (string.IsNullOrWhiteSpace(businessMessageId))
        {
            _logger.KafkaProcessMessageAsyncMissingBusinessMessageIdHeaderWarning(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value);
        }

        MessageIdentity messageIdentity = new(
            consumeResult.Topic,
            consumeResult.Partition.Value,
            consumeResult.Offset.Value,
            businessMessageId);

        if (_idempotencyStore != null)
        {
            try
            {
                bool hasBeenProcessed = await _idempotencyStore.HasBeenProcessedAsync(messageIdentity, cancellationToken).ConfigureAwait(false);
                if (hasBeenProcessed)
                {
                    _logger.KafkaProcessMessageAsyncMessageAlreadyProcessedInfo(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value);
                    StoreOffset(consumer, consumeResult);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.KafkaProcessMessageAsyncHasBeenProcessedCanceledInfo(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value);
                throw;
            }
            catch (Exception ex)
            {
                _logger.KafkaProcessMessageAsyncHasBeenProcessedException(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, ex);
                throw;
            }
        }

        long attempts = 0;
        while (!cancellationToken.IsCancellationRequested && (_options.HandlerErrorMaxRetries < 0 || attempts++ <= _options.HandlerErrorMaxRetries))
        {
            try
            {
                await _messageHandler.HandleAsync(consumeResult, cancellationToken).ConfigureAwait(false);
                StoreOffset(consumer, consumeResult);
                return;
            }
            catch (OperationCanceledException)
            {
                _logger.KafkaProcessMessageAsyncHandlerCanceledInfo(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value);
                throw;
            }
            catch (Exception ex)
            {
                _logger.KafkaProcessMessageAsyncHandlerException(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, ex);

                if (_options.HandlerErrorBackoffMs >= 0)
                {
                    try
                    {
                        await Task.Delay(_options.HandlerErrorBackoffMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.KafkaProcessMessageAsyncHandlerErrorTaskDelayCanceledInfo(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value);
                        throw;
                    }
                    catch (Exception e)
                    {
                        _logger.KafkaProcessMessageAsyncHandlerErrorTaskDelayException(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, e);
                        throw;
                    }
                }
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _logger.KafkaProcessMessageAsyncCancellationRequestedExitInfo(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, attempts, _options.HandlerErrorMaxRetries);
            return;
        }

        _logger.KafkaProcessMessageAsyncHandlerErrorExceedMaxRetriesWarning(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, attempts, _options.HandlerErrorMaxRetries);
        StoreOffset(consumer, consumeResult);
    }

    protected virtual void StoreOffset(IConsumer<string, TValue> consumer, ConsumeResult<string, TValue> consumeResult)
    {
        try
        {
            consumer.StoreOffset(consumeResult);
        }
        catch (TopicPartitionOffsetException ex)
        {
            if (ex.Error.IsFatal)
            {
                _logger.KafkaStoreOffsetFatalTopicPartitionOffsetException(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, ex);
            }
            else
            {
                _logger.KafkaStoreOffsetNonFatalTopicPartitionOffsetException(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, ex);
            }
        }
        catch (KafkaException ex)
        {
            if (ex.Error.IsFatal)
            {
                _logger.KafkaStoreOffsetFatalKafkaException(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, ex);
            }
            else
            {
                _logger.KafkaStoreOffsetNonFatalKafkaException(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, ex);
            }
        }
        catch (Exception ex)
        {
            _logger.KafkaStoreOffsetException(_options.GroupId, consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, ex);
        }
    }

    private static string? ReadBusinessMessageIdHeader(ConsumeResult<string, TValue> consumeResult)
    {
        if (consumeResult.Message?.Headers == null)
        {
            return null;
        }

#pragma warning disable IDE0046 // Convert to conditional expression
        if (!consumeResult.Message.Headers.TryGetLastBytes(KafkaConsumerHeaders.BusinessMessageId, out byte[] bytes))
        {
            return null;
        }
#pragma warning restore IDE0046 // Convert to conditional expression

        return bytes == null || bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
    }

    private IDeserializer<TValue> GetValueDeserializer()
    {
        // Application-level deserialization (JSON / Avro / Proto / etc.) lives in the handler when TValue is byte[]/string
        // Other TValue go through KafkaJsonDeserializer<T> with the supplied JsonSerializerOptions so converters
        // (e.g. DecimalJsonConverter, JsonStringEnumConverter) match the producer envelope's serialization
#pragma warning disable IDE0046 // Convert to conditional expression
        if (typeof(TValue) == typeof(byte[]))
        {
            return (IDeserializer<TValue>)(object)Deserializers.ByteArray;
        }
        else if (typeof(TValue) == typeof(string))
        {
            return (IDeserializer<TValue>)(object)Deserializers.Utf8;
        }
        else
        {
            return new KafkaJsonDeserializer<TValue>(_jsonSerializerOptions);
        }
#pragma warning restore IDE0046 // Convert to conditional expression
    }

    private void HandleConsumerError(IConsumer<string, TValue> _consumer, Error error)
    {
        if (error.IsFatal)
        {
            _logger.KafkaHandleConsumerErrorFatalError(_options.GroupId, error.Code, error.Reason);
            _ = Interlocked.Exchange(ref _fatalError, 1);
            return;
        }

        _logger.KafkaHandleConsumerErrorNonFatalError(_options.GroupId, error.Code, error.Reason);
    }

    private void HandlePartitionsRevoked(
        IConsumer<string, TValue> consumer,
        List<TopicPartitionOffset> partitions)
    {
        _logger.KafkaHandlePartitionsRevokedEntryDebug(_options.GroupId, partitions.Count);

        // Synchronous: handlers run inline on the poll thread, so no in-flight handler can be running on these
        // partitions when this callback fires. Flush stored offsets so the soon-to-be-revoked partitions don't
        // get redelivered to whoever picks them up next
        CommitAllOffsets(consumer);

        // Do not return anything with CooperativeSticky
    }
}

public static partial class KafkaConsumerServiceLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "KafkaConsumeAsync: consume already invoked for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeAsyncConsumeAlreadyInvokedInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Information,
        Message = "KafkaConsumeAsync: consume internal canceled for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeAsyncConsumeInternalCanceledInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Error,
        Message = "KafkaConsumeAsync: consume internal exception for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeAsyncConsumeInternalException(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, Exception exception);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Information,
        Message = "KafkaConsumeAsync: rebuild backoff reset for consumer group `{GroupId}` with topics `{Topics}` after run duration `{RunDurationMs}` to initial value `{InitialRebuildBackoffMs}`")]
    public static partial void KafkaConsumeAsyncRebuildBackoffResetInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, int runDurationMs, int initialRebuildBackoffMs);

    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Information,
        Message = "KafkaConsumeAsync: cancellation requested for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeAsyncCancellationRequestedInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 1060,
        Level = LogLevel.Information,
        Message = "KafkaConsumeAsync: rebuild backoff for consumer group `{GroupId}` with topics `{Topics}` of `{RebuildBackoffMs}` ms")]
    public static partial void KafkaConsumeAsyncRebuildBackoffInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, int rebuildBackoffMs);

    [LoggerMessage(
        EventId = 1070,
        Level = LogLevel.Information,
        Message = "KafkaConsumeAsync: task delay canceled for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeAsyncTaskDelayCanceledInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 1080,
        Level = LogLevel.Error,
        Message = "KafkaConsumeAsync: task delay exception for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeAsyncTaskDelayException(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, Exception exception);

    [LoggerMessage(
        EventId = 1090,
        Level = LogLevel.Information,
        Message = "KafkaConsumeAsync: cancellation requested exit for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeAsyncCancellationRequestedExitInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    /*
    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Debug,
        Message = "KafkaConsumerService: log for consumer group `{GroupId}` at facility `{LogFacility}` with message `{LogMessage}``")]
    public static partial void KafkaConsumerServiceConsumerDebug(
        this ILogger logger, string groupId, string logFacility, string logMessage);
    */

    [LoggerMessage(
        EventId = 2020,
        Level = LogLevel.Debug,
        Message = "KafkaConsumerService: new partition assignment for consumer group `{GroupId}` with `{PartitionsCount}` partitions")]
    public static partial void KafkaConsumerServiceConsumerPartitionsAssignedDebug(
        this ILogger logger, string groupId, int partitionsCount);

    [LoggerMessage(
        EventId = 2030,
        Level = LogLevel.Debug,
        Message = "KafkaConsumerService: partition assignment lost for consumer group `{GroupId}` with partitions `{Partitions}`")]
    public static partial void KafkaConsumerServiceConsumerPartitionsLostDebug(
        this ILogger logger, string groupId, List<TopicPartitionOffset> partitions);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Error,
        Message = "KafkaCommitAllOffsets: fatal topic partition offset exception for consumer group `{GroupId}`")]
    public static partial void KafkaCommitAllOffsetsFatalTopicPartitionOffsetException(
        this ILogger logger, string groupId, Exception exception);

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Error,
        Message = "KafkaCommitAllOffsets: non-fatal topic partition offset exception for consumer group `{GroupId}`")]
    public static partial void KafkaCommitAllOffsetsNonFatalTopicPartitionOffsetException(
        this ILogger logger, string groupId, Exception exception);

    [LoggerMessage(
        EventId = 3030,
        Level = LogLevel.Error,
        Message = "KafkaCommitAllOffsets: fatal kafka exception for consumer group `{GroupId}`")]
    public static partial void KafkaCommitAllOffsetsFatalKafkaException(
        this ILogger logger, string groupId, Exception exception);

    [LoggerMessage(
        EventId = 3040,
        Level = LogLevel.Error,
        Message = "KafkaCommitAllOffsets: non-fatal kafka exception for consumer group `{GroupId}`")]
    public static partial void KafkaCommitAllOffsetsNonFatalKafkaException(
        this ILogger logger, string groupId, Exception exception);

    [LoggerMessage(
        EventId = 3050,
        Level = LogLevel.Error,
        Message = "KafkaCommitAllOffsets: exception for consumer group `{GroupId}`")]
    public static partial void KafkaCommitAllOffsetsException(
        this ILogger logger, string groupId, Exception exception);

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Information,
        Message = "KafkaConsumeInternalAsync: subscribed topics for consumer group `{GroupId}`: {Topics}")]
    public static partial void KafkaConsumeInternalAsyncSubscribeInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Error,
        Message = "KafkaConsumeInternalAsync: consume fatal consume exception for consumer group `{GroupId}` with topics {Topics}")]
    public static partial void KafkaConsumeInternalAsyncConsumeFatalConsumeException(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, ConsumeException exception);

    [LoggerMessage(
        EventId = 4030,
        Level = LogLevel.Error,
        Message = "KafkaConsumeInternalAsync: consume non-fatal consume exception for consumer group `{GroupId}` with topics {Topics}")]
    public static partial void KafkaConsumeInternalAsyncConsumeNonFatalConsumeException(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, ConsumeException exception);

    [LoggerMessage(
        EventId = 4040,
        Level = LogLevel.Error,
        Message = "KafkaConsumeInternalAsync: consume exception for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeInternalAsyncConsumeException(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, Exception exception);

    [LoggerMessage(
        EventId = 4050,
        Level = LogLevel.Debug,
        Message = "KafkaConsumeInternalAsync: null consume result for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeInternalAsyncNullConsumeResultDebug(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 4060,
        Level = LogLevel.Information,
        Message = "KafkaConsumeInternalAsync: fatal error exit for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeInternalAsyncFatalErrorExitInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 4070,
        Level = LogLevel.Information,
        Message = "KafkaConsumeInternalAsync: cancellation requested exit for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeInternalAsyncCancellationRequestedExitInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 4080,
        Level = LogLevel.Information,
        Message = "KafkaConsumeInternalAsync: closing for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeInternalAsyncClosingInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 4090,
        Level = LogLevel.Information,
        Message = "KafkaConsumeInternalAsync: closed for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeInternalAsyncClosedInfo(
        this ILogger logger, string groupId, IReadOnlyList<string> topics);

    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Error,
        Message = "KafkaConsumeInternalAsync: fatal close kafka exception for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeInternalAsyncCloseFatalKafkaException(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, KafkaException ex);

    [LoggerMessage(
        EventId = 4110,
        Level = LogLevel.Error,
        Message = "KafkaConsumeInternalAsync: non-fatal close kafka exception for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeInternalAsyncCloseNonFatalKafkaException(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, KafkaException ex);

    [LoggerMessage(
        EventId = 4120,
        Level = LogLevel.Error,
        Message = "KafkaConsumeInternalAsync: close exception for consumer group `{GroupId}` with topics `{Topics}`")]
    public static partial void KafkaConsumeInternalAsyncCloseException(
        this ILogger logger, string groupId, IReadOnlyList<string> topics, Exception ex);

    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Warning,
        Message = "KafkaHandleDeserializationFailure: skipping deserialization failure for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}` and error code `{ErrorCode}`")]
    public static partial void KafkaHandleDeserializationFailureSkippingDeserializationFailureWarning(
        this ILogger logger, string groupId, string topic, int partition, long offset, ErrorCode errorCode, ConsumeException exception);

    [LoggerMessage(
        EventId = 5020,
        Level = LogLevel.Error,
        Message = "KafkaHandleDeserializationFailure: fatal topic partition offset exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaHandleDeserializationFailureFatalTopicPartitionOffsetException(
        this ILogger logger, string groupId, string topic, int partition, long offset, TopicPartitionOffsetException exception);

    [LoggerMessage(
        EventId = 5030,
        Level = LogLevel.Error,
        Message = "KafkaHandleDeserializationFailure: non-fatal topic partition offset exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaHandleDeserializationFailureNonFatalTopicPartitionOffsetException(
        this ILogger logger, string groupId, string topic, int partition, long offset, TopicPartitionOffsetException exception);

    [LoggerMessage(
        EventId = 5040,
        Level = LogLevel.Error,
        Message = "KafkaHandleDeserializationFailure: fatal kafka exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaHandleDeserializationFailureFatalKafkaException(
        this ILogger logger, string groupId, string topic, int partition, long offset, KafkaException exception);

    [LoggerMessage(
        EventId = 5050,
        Level = LogLevel.Error,
        Message = "KafkaHandleDeserializationFailure: non-fatal kafka exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaHandleDeserializationFailureNonFatalKafkaException(
        this ILogger logger, string groupId, string topic, int partition, long offset, KafkaException exception);

    [LoggerMessage(
        EventId = 5060,
        Level = LogLevel.Error,
        Message = "KafkaHandleDeserializationFailure: exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaHandleDeserializationFailureException(
        this ILogger logger, string groupId, string topic, int partition, long offset, Exception exception);

    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Error,
        Message = "KafkaInvalidateCachedBootstrapServers: exception for consumer group `{GroupId}`")]
    public static partial void KafkaInvalidateCachedBootstrapServersException(
        this ILogger logger, string groupId, Exception exception);

    [LoggerMessage(
        EventId = 7010,
        Level = LogLevel.Information,
        Message = "KafkaProcessMessageAsync: missing business message id header for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaProcessMessageAsyncMissingBusinessMessageIdHeaderWarning(
        this ILogger logger, string groupId, string topic, int partition, long offset);

    [LoggerMessage(
        EventId = 7020,
        Level = LogLevel.Information,
        Message = "KafkaProcessMessageAsync: message already processed for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaProcessMessageAsyncMessageAlreadyProcessedInfo(
        this ILogger logger, string groupId, string topic, int partition, long offset);

    [LoggerMessage(
        EventId = 7030,
        Level = LogLevel.Information,
        Message = "KafkaProcessMessageAsync: has been processed canceled for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaProcessMessageAsyncHasBeenProcessedCanceledInfo(
        this ILogger logger, string groupId, string topic, int partition, long offset);

    [LoggerMessage(
        EventId = 7040,
        Level = LogLevel.Error,
        Message = "KafkaProcessMessageAsync: has been processed exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaProcessMessageAsyncHasBeenProcessedException(
        this ILogger logger, string groupId, string topic, int partition, long offset, Exception exception);

    [LoggerMessage(
        EventId = 7050,
        Level = LogLevel.Information,
        Message = "KafkaProcessMessageAsync: handler canceled for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaProcessMessageAsyncHandlerCanceledInfo(
        this ILogger logger, string groupId, string topic, int partition, long offset);

    [LoggerMessage(
        EventId = 7060,
        Level = LogLevel.Error,
        Message = "KafkaProcessMessageAsync: handler exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaProcessMessageAsyncHandlerException(
        this ILogger logger, string groupId, string topic, int partition, long offset, Exception exception);

    [LoggerMessage(
        EventId = 7070,
        Level = LogLevel.Information,
        Message = "KafkaProcessMessageAsync: handler error task delay canceled for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaProcessMessageAsyncHandlerErrorTaskDelayCanceledInfo(
        this ILogger logger, string groupId, string topic, int partition, long offset);

    [LoggerMessage(
        EventId = 7080,
        Level = LogLevel.Error,
        Message = "KafkaProcessMessageAsync: handler error task delay exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaProcessMessageAsyncHandlerErrorTaskDelayException(
        this ILogger logger, string groupId, string topic, int partition, long offset, Exception exception);

    [LoggerMessage(
        EventId = 7090,
        Level = LogLevel.Information,
        Message = "KafkaProcessMessageAsync: cancellation requested exit for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`: `{Attempts}` of `{MaxRetries}`")]
    public static partial void KafkaProcessMessageAsyncCancellationRequestedExitInfo(
        this ILogger logger, string groupId, string topic, int partition, long offset, long attempts, int maxRetries);

    [LoggerMessage(
        EventId = 7100,
        Level = LogLevel.Warning,
        Message = "KafkaProcessMessageAsync: handler error exceed max retries, advancing offset and dropping poison message for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`: `{Attempts}` of `{MaxRetries}`")]
    public static partial void KafkaProcessMessageAsyncHandlerErrorExceedMaxRetriesWarning(
        this ILogger logger, string groupId, string topic, int partition, long offset, long attempts, int maxRetries);

    [LoggerMessage(
        EventId = 8010,
        Level = LogLevel.Error,
        Message = "KafkaStoreOffset: fatal topic partition offset exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaStoreOffsetFatalTopicPartitionOffsetException(
        this ILogger logger, string groupId, string topic, int partition, long offset, KafkaException exception);

    [LoggerMessage(
        EventId = 8020,
        Level = LogLevel.Error,
        Message = "KafkaStoreOffset: non-fatal kafka exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaStoreOffsetNonFatalTopicPartitionOffsetException(
        this ILogger logger, string groupId, string topic, int partition, long offset, KafkaException exception);

    [LoggerMessage(
        EventId = 8030,
        Level = LogLevel.Error,
        Message = "KafkaStoreOffset: fatal kafka exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaStoreOffsetFatalKafkaException(
        this ILogger logger, string groupId, string topic, int partition, long offset, KafkaException exception);

    [LoggerMessage(
        EventId = 8040,
        Level = LogLevel.Error,
        Message = "KafkaStoreOffset: non-fatal kafka exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaStoreOffsetNonFatalKafkaException(
        this ILogger logger, string groupId, string topic, int partition, long offset, KafkaException exception);

    [LoggerMessage(
        EventId = 8050,
        Level = LogLevel.Error,
        Message = "KafkaStoreOffset: exception for consumer group `{GroupId}` with topic `{Topic}` in partition `{Partition}` at offset `{Offset}`")]
    public static partial void KafkaStoreOffsetException(
        this ILogger logger, string groupId, string topic, int partition, long offset, Exception exception);

    [LoggerMessage(
        EventId = 9010,
        Level = LogLevel.Error,
        Message = "KafkaHandleConsumerError: fatal error for consumer group `{GroupId}` with code `{ErrorCode}` and reason `{ErrorReason}`")]
    public static partial void KafkaHandleConsumerErrorFatalError(
        this ILogger logger, string groupId, ErrorCode errorCode, string errorReason);

    [LoggerMessage(
        EventId = 9020,
        Level = LogLevel.Error,
        Message = "KafkaHandleConsumerError: non-fatal error for consumer group `{GroupId}` with code `{ErrorCode}` and reason `{ErrorReason}`")]
    public static partial void KafkaHandleConsumerErrorNonFatalError(
        this ILogger logger, string groupId, ErrorCode errorCode, string errorReason);

    [LoggerMessage(
        EventId = 10010,
        Level = LogLevel.Debug,
        Message = "KafkaHandlePartitionsRevoked: partition assignment revoked for consumer group `{GroupId}` with `{PartitionsCount}` partitions")]
    public static partial void KafkaHandlePartitionsRevokedEntryDebug(
        this ILogger logger, string groupId, int partitionsCount);
}
