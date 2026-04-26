using Amazon.Kafka;
using Amazon.Kafka.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;

namespace Dalmarkit.Messaging.Kafka.Config;

/// <summary>
/// Provides MSK bootstrap server addresses by calling
/// <see cref="IAmazonKafka.GetBootstrapBrokersAsync(GetBootstrapBrokersRequest, CancellationToken)"/>
/// with the cluster ARN from <see cref="AwsKafkaOptions.ClusterArn"/>
/// Returns the SASL/IAM broker string for MSK IAM authentication
/// The result is cached after the first successful retrieval until <see cref="InvalidateCachedBootstrapServers"/> is called
/// No need to dispose SemaphoreSlim if AvailableWaitHandle is not called
/// https://github.com/dotnet/runtime/blob/v10.0.6/src/libraries/System.Private.CoreLib/src/System/Threading/SemaphoreSlim.cs
/// </summary>
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
public class AwsSdkSaslIamBootstrapServersProvider : IBootstrapServersProvider
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
{
    private readonly IAmazonKafka _kafkaClient;
    private readonly AwsKafkaOptions _connectionOptions;
    private readonly ILogger<AwsSdkSaslIamBootstrapServersProvider> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private volatile string? _cachedBootstrapServers;

    /// <summary>
    /// Initializes a new instance of the <see cref="AwsSdkSaslIamBootstrapServersProvider"/> class
    /// </summary>
    /// <param name="kafkaClient">Amazon Kafka client used to call GetBootstrapBrokers</param>
    /// <param name="connectionOptions">Kafka connection options containing the cluster ARN</param>
    /// <param name="logger">Logger instance</param>
    public AwsSdkSaslIamBootstrapServersProvider(
        IAmazonKafka kafkaClient,
        IOptions<AwsKafkaOptions> connectionOptions,
        ILogger<AwsSdkSaslIamBootstrapServersProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(kafkaClient);
        ArgumentNullException.ThrowIfNull(connectionOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _kafkaClient = kafkaClient;
        _logger = logger;

        _connectionOptions = connectionOptions.Value;
        _connectionOptions.Validate();
    }

    /// <inheritdoc />
    public async Task<string> GetBootstrapServersAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedBootstrapServers is not null)
        {
            return _cachedBootstrapServers;
        }

        try
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.GetBootstrapServersAsyncSemaphoreWaitCanceledInfo(_connectionOptions.ClusterArn);
            throw;
        }
        catch (Exception ex)
        {
            _logger.GetBootstrapServersAsyncSemaphoreWaitException(_connectionOptions.ClusterArn, ex);
            throw;
        }

        try
        {
            if (_cachedBootstrapServers is not null)
            {
                return _cachedBootstrapServers;
            }

            if (string.IsNullOrWhiteSpace(_connectionOptions.ClusterArn))
            {
                _logger.GetBootstrapServersAsyncNullClusterArnError();
                throw new InvalidOperationException("Cluster ARN is required");
            }

            _logger.GetBootstrapServersAsyncRetrievingBootstrapServersInfo(_connectionOptions.ClusterArn);

            GetBootstrapBrokersRequest getBootstrapBrokerRequest = new()
            {
                ClusterArn = _connectionOptions.ClusterArn
            };
            GetBootstrapBrokersResponse response = await _kafkaClient.GetBootstrapBrokersAsync(getBootstrapBrokerRequest, cancellationToken).ConfigureAwait(false);
            if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
            {
                _logger.GetBootstrapServersAsyncHttpStatusCodeError(_connectionOptions.ClusterArn, response.HttpStatusCode);
                throw new HttpRequestException(
                    $"Error status {response.HttpStatusCode} when getting bootstrap brokers for cluster {_connectionOptions.ClusterArn}");
            }

            string? bootstrapBrokers = string.IsNullOrWhiteSpace(response.BootstrapBrokerStringPublicSaslIam)
                ? response.BootstrapBrokerStringSaslIam
                : response.BootstrapBrokerStringPublicSaslIam;

            if (string.IsNullOrWhiteSpace(bootstrapBrokers))
            {
                _logger.GetBootstrapServersAsyncNullBootstrapBrokersError(
                    _connectionOptions.ClusterArn,
                    response.HttpStatusCode,
                    response.ResponseMetadata?.RequestId);
                throw new InvalidOperationException(
                    $"No bootstrap brokers found for AWS MSK cluster {_connectionOptions.ClusterArn}");
            }

            _cachedBootstrapServers = bootstrapBrokers;

            _logger.GetBootstrapServersAsyncRetrievedBootstrapServersInfo(_connectionOptions.ClusterArn, bootstrapBrokers);

            return bootstrapBrokers;
        }
        catch (OperationCanceledException)
        {
            _logger.GetBootstrapServersAsyncGetBootstrapServersCanceledInfo(_connectionOptions.ClusterArn);
            throw;
        }
        catch (Exception ex)
        {
            _logger.GetBootstrapServersAsyncGetBootstrapServersException(_connectionOptions.ClusterArn, ex);
            throw;
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateCachedBootstrapServers()
    {
        _cachedBootstrapServers = null;

        _logger.InvalidateCachedBootstrapServersInvalidatedInfo(_connectionOptions.ClusterArn);
    }
}

public static partial class AwsSdkSaslIamBootstrapServersProviderLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "GetBootstrapServersAsync: semaphore wait canceled for AWS MSK cluster `{ClusterArn}`")]
    public static partial void GetBootstrapServersAsyncSemaphoreWaitCanceledInfo(
        this ILogger logger, string clusterArn);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Error,
        Message = "GetBootstrapServersAsync: semaphore wait exception for AWS MSK cluster `{ClusterArn}`")]
    public static partial void GetBootstrapServersAsyncSemaphoreWaitException(
        this ILogger logger, string clusterArn, Exception exception);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Error,
        Message = "GetBootstrapServersAsync: null cluster ARN")]
    public static partial void GetBootstrapServersAsyncNullClusterArnError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Information,
        Message = "GetBootstrapServersAsync: retrieving bootstrap servers for AWS MSK cluster `{ClusterArn}`")]
    public static partial void GetBootstrapServersAsyncRetrievingBootstrapServersInfo(
        this ILogger logger, string clusterArn);

    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Error,
        Message = "GetBootstrapServersAsync: error HTTP status code when getting bootstrap servers for AWS MSK cluster `{ClusterArn}`: {HttpStatusCode}")]
    public static partial void GetBootstrapServersAsyncHttpStatusCodeError(
        this ILogger logger, string clusterArn, HttpStatusCode httpStatusCode);

    [LoggerMessage(
        EventId = 1060,
        Level = LogLevel.Error,
        Message = "GetBootstrapServersAsync: null SASL/IAM bootstrap servers for AWS MSK cluster `{ClusterArn}` with HTTP status `{HttpStatusCode}` and request id `{RequestId}`")]
    public static partial void GetBootstrapServersAsyncNullBootstrapBrokersError(
        this ILogger logger, string clusterArn, HttpStatusCode httpStatusCode, string? requestId);

    [LoggerMessage(
        EventId = 1070,
        Level = LogLevel.Information,
        Message = "GetBootstrapServersAsync: retrieved SASL/IAM bootstrap servers for AWS MSK cluster `{ClusterArn}`: {BootstrapBrokers}")]
    public static partial void GetBootstrapServersAsyncRetrievedBootstrapServersInfo(
        this ILogger logger, string clusterArn, string bootstrapBrokers);

    [LoggerMessage(
        EventId = 1080,
        Level = LogLevel.Information,
        Message = "GetBootstrapServersAsync: get bootstrap servers canceled for AWS MSK cluster `{ClusterArn}`")]
    public static partial void GetBootstrapServersAsyncGetBootstrapServersCanceledInfo(
        this ILogger logger, string clusterArn);

    [LoggerMessage(
        EventId = 1090,
        Level = LogLevel.Error,
        Message = "GetBootstrapServersAsync: get bootstrap servers exception for AWS MSK cluster `{ClusterArn}`")]
    public static partial void GetBootstrapServersAsyncGetBootstrapServersException(
        this ILogger logger, string clusterArn, Exception exception);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Information,
        Message = "InvalidateCachedBootstrapServers: cached bootstrap servers invalidated for AWS MSK cluster `{ClusterArn}`")]
    public static partial void InvalidateCachedBootstrapServersInvalidatedInfo(
        this ILogger logger, string clusterArn);
}
