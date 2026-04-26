using Confluent.Kafka;
using Dalmarkit.Messaging.Kafka.Config;
using Dalmarkit.Messaging.Kafka.Handlers;
using Dalmarkit.Messaging.Kafka.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace Dalmarkit.Messaging.Kafka.Producers;

/// <summary>
/// Confluent.Kafka-based producer with AWS MSK IAM authentication
/// Keys are serialized as UTF-8 strings and values are serialized with <see cref="KafkaJsonSerializer{T}"/>
/// The producer is idempotent (acks=all, enable.idempotence=true) by default
/// On a fatal librdkafka error the underlying producer is disposed and rebuilt on the next call
/// SCRAM-SHA-512 is the only supported SASL mechanism for non-AWS clusters
/// </summary>
/// <typeparam name="TValue">Value type</typeparam>
public class KafkaProducerService<TValue> : IKafkaProducerService<TValue>
{
    /// <summary>
    /// DI key used to look up the <see cref="JsonSerializerOptions"/> applied by <see cref="KafkaJsonSerializer{T}"/>
    /// Register a keyed singleton under this key from the host so Kafka payload serialization can be configured (e.g. decimal/enum converters) consistently with other transports
    /// </summary>
#pragma warning disable RCS1158 // Static member in generic type should use a type parameter
    public const string JsonSerializerOptionsServiceKey = "Dalmarkit.Messaging.Kafka.Producers";
#pragma warning restore RCS1158 // Static member in generic type should use a type parameter

    private readonly IBootstrapServersProvider _bootstrapServersProvider;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly KafkaProducerOptions _options;
    private readonly ILogger<KafkaProducerService<TValue>> _logger;
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private readonly IOauthHandlers _oauthHandlers;

    private volatile int _isDisposed;

    private IProducer<string, TValue>? _producer;

    /// <summary>
    /// Creates a new producer. The underlying <see cref="IProducer{TKey,TValue}"/> is built lazily on first use
    /// Lives for the lifetime of this instance unless a fatal error forces a rebuild.
    /// </summary>
    /// <param name="bootstrapServersProvider">Retrieve list of Kafka bootstrap servers</param>
    /// <param name="jsonSerializerOptions">JSON serializer options for payload values (resolved from DI container under <see cref="JsonSerializerOptionsServiceKey"/> so transports can share converters)</param>
    /// <param name="oauthHandlers">OAuth handlers</param>
    /// <param name="options">Kafka producer options</param>
    /// <param name="logger">Logger</param>
    public KafkaProducerService(
        IBootstrapServersProvider bootstrapServersProvider,
        [FromKeyedServices(JsonSerializerOptionsServiceKey)] JsonSerializerOptions jsonSerializerOptions,
        IOauthHandlers oauthHandlers,
        IOptions<KafkaProducerOptions> options,
        ILogger<KafkaProducerService<TValue>> logger)
    {
        ArgumentNullException.ThrowIfNull(bootstrapServersProvider);
        ArgumentNullException.ThrowIfNull(jsonSerializerOptions);
        ArgumentNullException.ThrowIfNull(oauthHandlers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _bootstrapServersProvider = bootstrapServersProvider;
        _jsonSerializerOptions = jsonSerializerOptions;
        _oauthHandlers = oauthHandlers;
        _logger = logger;

        _options = options.Value;
        _options.Validate();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        if (!disposing)
        {
            return;
        }

        IProducer<string, TValue>? producer = Interlocked.Exchange(ref _producer, null);
        if (producer != null)
        {
            try
            {
                // Block up to ShutdownFlushTimeoutMs so idempotent in-flight retries get a chance to complete before dropping messages on shutdown
                // Bound by ShutdownFlushTimeoutMs which should configure strictly less than HostOptions.ShutdownTimeout so that host does not SIGKILL process while still flushing
                _ = producer.Flush(TimeSpan.FromMilliseconds(_options.ShutdownFlushTimeoutMs));
            }
            catch (Exception ex)
            {
                _logger.KafkaProducerDisposeFlushException(ex);
            }

            try
            {
                producer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.KafkaProducerDisposeDisposeException(ex);
            }
        }

        // No need to dispose SemaphoreSlim if AvailableWaitHandle is not called
        // https://github.com/dotnet/runtime/blob/v10.0.6/src/libraries/System.Private.CoreLib/src/System/Threading/SemaphoreSlim.cs
    }

    /// <inheritdoc />
    public Task<DeliveryResult<string, TValue>> ProduceAsync(
        string topic,
        string? key,
        TValue value,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        Message<string, TValue> message = new()
        {
            // Null intentional: defers partitioning to librdkafka's configured partitioner instead of hashing empty string to single partition
            Key = key!,
            Value = value,
        };

        return ProduceAsync(topic, message, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DeliveryResult<string, TValue>> ProduceAsync(
        string topic,
        Message<string, TValue> message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            IProducer<string, TValue> producer = await GetProducerAsync(cancellationToken).ConfigureAwait(false);
            DeliveryResult<string, TValue> result = await producer
                .ProduceAsync(topic, message, cancellationToken)
                .ConfigureAwait(false);

            _logger.KafkaProducerProduceAsyncProduceMessageDebug(
                result.Topic,
                result.Partition.Value,
                result.Offset.Value);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.KafkaProducerProduceAsyncProduceCanceledInfo(topic);
            throw;
        }
        catch (ProduceException<string, TValue> ex)
        {
            _logger.KafkaProducerProduceAsyncProduceProduceException(topic, ex);
            throw;
        }
        catch (Exception ex)
        {
            _logger.KafkaProducerProduceAsyncProduceException(topic, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        IProducer<string, TValue> producer = await GetProducerAsync(cancellationToken).ConfigureAwait(false);

        // Compose caller token with FlushTimeoutMs into linked CTS so cancellation actually interrupts blocking Flush
        // Caller-token cancellation propagates as OperationCanceledException
        // Timeout-only firing surfaces as TimeoutException via exception filter below
        // Offload to the thread pool so the awaiting caller is not pinned to a thread for the full flush window
        using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(_options.FlushTimeoutMs));
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        try
        {
            await Task.Run(() => producer.Flush(linkedCts.Token), linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.KafkaProducerFlushAsyncTimeoutWarning(_options.FlushTimeoutMs);
            throw new TimeoutException($"Kafka flush did not complete within {_options.FlushTimeoutMs} ms");
        }
    }

    protected string BuildClientId(string serviceName)
    {
        string hostname = Dns.GetHostName();
        return $"{serviceName}-{hostname}";
    }

    protected ProducerBuilder<string, TValue> BuildProducer(ProducerConfig producerConfig)
    {
        ProducerBuilder<string, TValue> builder = new ProducerBuilder<string, TValue>(producerConfig)
            .SetKeySerializer(Serializers.Utf8)
            .SetValueSerializer(new KafkaJsonSerializer<TValue>(_jsonSerializerOptions))
            .SetErrorHandler(HandleProducerError);
        // https://github.com/confluentinc/confluent-kafka-dotnet/issues/1834 - SetLogHandler crashes librdkafka in some configs
        // .SetLogHandler((_, logMessage) => _logger.KafkaProducerServiceProducerDebug(logMessage.Facility, logMessage.Message))

        // Only attach the AWS MSK OAuth refresh handler when the producer is actually configured for OAuthBearer.
        // Local-dev / non-AWS clusters use SASL/SCRAM and must not run the AWS token refresh on every reconnect.
        if (producerConfig.SaslMechanism == SaslMechanism.OAuthBearer)
        {
            _ = builder.SetOAuthBearerTokenRefreshHandler(_oauthHandlers.GetOauthBearerTokenRefreshHandler(_options.Region!));
        }

        return builder;
    }

    protected async Task<ProducerConfig> BuildProducerConfig(
        KafkaProducerOptions options,
        CancellationToken cancellationToken = default)
    {
        bool hasBootstrapServersConfig = !string.IsNullOrWhiteSpace(options.BootstrapServers);

        string bootstrapServers = hasBootstrapServersConfig
            ? options.BootstrapServers!
            : await _bootstrapServersProvider.GetBootstrapServersAsync(cancellationToken).ConfigureAwait(false);

        ProducerConfig producerConfig = new()
        {
            SaslMechanism = hasBootstrapServersConfig ? SaslMechanism.ScramSha512 : SaslMechanism.OAuthBearer,
            Acks = Acks.All,
            ClientId = BuildClientId(options.ServiceName),
            BootstrapServers = bootstrapServers,
            SecurityProtocol = SecurityProtocol.SaslSsl,
            RequestTimeoutMs = options.RequestTimeoutMs,
            MessageTimeoutMs = options.MessageTimeoutMs,
            EnableIdempotence = true,
            LingerMs = options.LingerMs,
            MessageSendMaxRetries = options.MessageSendMaxRetries,
            CompressionType = options.CompressionType,
        };

        if (hasBootstrapServersConfig)
        {
            producerConfig.SaslUsername = options.SaslUsername;
            producerConfig.SaslPassword = options.SaslPassword;
        }

        return producerConfig;
    }

    protected async Task<IProducer<string, TValue>> GetProducerAsync(CancellationToken cancellationToken = default)
    {
        IProducer<string, TValue>? currentProducer = Volatile.Read(ref _producer);
        if (currentProducer != null)
        {
            return currentProducer;
        }

        try
        {
            await _initSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.GetProducerAsyncSemaphoreWaitCanceledInfo();
            throw;
        }
        catch (Exception ex)
        {
            _logger.GetProducerAsyncSemaphoreWaitException(ex);
            throw;
        }

        try
        {
            currentProducer = Volatile.Read(ref _producer);
            if (currentProducer != null)
            {
                return currentProducer;
            }

            ProducerConfig producerConfig = await BuildProducerConfig(_options, cancellationToken).ConfigureAwait(false);
            ProducerBuilder<string, TValue> builder = BuildProducer(producerConfig);
            IProducer<string, TValue> builtProducer = builder.Build();
            Volatile.Write(ref _producer, builtProducer);

            return builtProducer;
        }
        catch (OperationCanceledException)
        {
            _logger.GetProducerAsyncBuildProducerCanceledInfo();
            throw;
        }
        catch (Exception ex)
        {
            _logger.GetProducerAsyncBuildProducerException(ex);
            throw;
        }
        finally
        {
            _ = _initSemaphore.Release();
        }
    }

    private void HandleProducerError(IProducer<string, TValue> faultedProducer, Error error)
    {
        if (!error.IsFatal)
        {
            _logger.HandleProducerErrorNonFatalWarning(error.Code, error.Reason);
            return;
        }

        _logger.HandleProducerErrorFatalError(error.Code, error.Reason);

        // CAS so we only dispose the producer instance we observed as faulted; if another thread already
        // swapped or nullified the field we leave it alone.
        IProducer<string, TValue>? previousProducer = Interlocked.CompareExchange(ref _producer, null, faultedProducer);
        if (!ReferenceEquals(previousProducer, faultedProducer))
        {
            return;
        }

        // Invalidate cached broker list so that rebuild path re-fetches from AWS in case cluster was scaled or failed over between the original lookup and this fatal error
        try
        {
            _bootstrapServersProvider.InvalidateCachedBootstrapServers();
        }
        catch (Exception ex)
        {
            _logger.HandleProducerErrorInvalidateCachedBootstrapServersException(ex);
        }

        _ = Task.Run(() =>
        {
            try
            {
                faultedProducer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.HandleProducerErrorDisposeFaultedProducerException(ex);
            }
        });
    }
}

public static partial class KafkaProducerServiceLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "KafkaProducerDispose: flush Kafka producer exception")]
    public static partial void KafkaProducerDisposeFlushException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Error,
        Message = "KafkaProducerDispose: dispose Kafka producer exception")]
    public static partial void KafkaProducerDisposeDisposeException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Debug,
        Message = "KafkaProducerProduceAsync: produced message for topic `{Topic}`, partition `{Partition}` and offset `{Offset}`")]
    public static partial void KafkaProducerProduceAsyncProduceMessageDebug(
        this ILogger logger, string topic, int partition, long offset);

    [LoggerMessage(
        EventId = 2020,
        Level = LogLevel.Information,
        Message = "KafkaProducerProduceAsync: produce message canceled for topic `{Topic}`")]
    public static partial void KafkaProducerProduceAsyncProduceCanceledInfo(
        this ILogger logger, string topic);

    [LoggerMessage(
        EventId = 2030,
        Level = LogLevel.Error,
        Message = "KafkaProducerProduceAsync: produce exception when produce message for topic `{Topic}`")]
    public static partial void KafkaProducerProduceAsyncProduceProduceException(
        this ILogger logger, string topic, Exception exception);

    [LoggerMessage(
        EventId = 2040,
        Level = LogLevel.Error,
        Message = "KafkaProducerProduceAsync: exception when produce message for topic `{Topic}`")]
    public static partial void KafkaProducerProduceAsyncProduceException(
        this ILogger logger, string topic, Exception exception);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Warning,
        Message = "KafkaProducerFlushAsync: flush did not complete within `{FlushTimeoutMs}` ms")]
    public static partial void KafkaProducerFlushAsyncTimeoutWarning(
        this ILogger logger, int flushTimeoutMs);

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Information,
        Message = "KafkaProducerGetProducerAsync: semaphore wait canceled")]
    public static partial void GetProducerAsyncSemaphoreWaitCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Error,
        Message = "KafkaProducerGetProducerAsync: semaphore wait exception")]
    public static partial void GetProducerAsyncSemaphoreWaitException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4030,
        Level = LogLevel.Information,
        Message = "KafkaProducerGetProducerAsync: build producer canceled")]
    public static partial void GetProducerAsyncBuildProducerCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 4040,
        Level = LogLevel.Error,
        Message = "KafkaProducerGetProducerAsync: build producer exception")]
    public static partial void GetProducerAsyncBuildProducerException(
        this ILogger logger, Exception exception);

    /*
    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Debug,
        Message = "KafkaProducerService: producer log at facility `{LogFacility}` with message `{LogMessage}`")]
    public static partial void KafkaProducerServiceProducerDebug(
        this ILogger logger, string logFacility, string logMessage);
    */

    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Warning,
        Message = "HandleProducerError: producer non-fatal error with code `{ErrorCode}` and reason `{ErrorReason}`")]
    public static partial void HandleProducerErrorNonFatalWarning(
        this ILogger logger, ErrorCode errorCode, string errorReason);

    [LoggerMessage(
        EventId = 6020,
        Level = LogLevel.Error,
        Message = "HandleProducerError: producer will be rebuilt on next call due to fatal error with code `{ErrorCode}` and reason `{ErrorReason}`")]
    public static partial void HandleProducerErrorFatalError(
        this ILogger logger, ErrorCode errorCode, string errorReason);

    [LoggerMessage(
        EventId = 6030,
        Level = LogLevel.Error,
        Message = "HandleProducerError: invalidate cached bootstrap servers exception")]
    public static partial void HandleProducerErrorInvalidateCachedBootstrapServersException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 6040,
        Level = LogLevel.Error,
        Message = "HandleProducerError: dispose faulted producer exception")]
    public static partial void HandleProducerErrorDisposeFaultedProducerException(
        this ILogger logger, Exception exception);
}
