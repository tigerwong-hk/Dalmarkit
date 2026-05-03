using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dalmarkit.Messaging.Kafka.Consumers;

/// <summary>
/// <see cref="BackgroundService"/> that drives an <see cref="IKafkaConsumerService{TValue}"/> for the lifetime of the host
/// Multiple instances registered under the same Kafka <c>group.id</c> scale horizontally
/// Broker distributes partitions among them
/// Never-supposed-to-happen unexpected fault propagates to BackgroundService, which faults the host (HostOptions.BackgroundServiceExceptionBehavior defaults to StopHost)
/// </summary>
/// <typeparam name="TValue">Value type</typeparam>
/// <param name="consumerService">Kafka consumer service</param>
/// <param name="logger">Kafka consumer background service logger</param>
public class KafkaConsumerBackgroundService<TValue>(
    IKafkaConsumerService<TValue> consumerService,
    ILogger<KafkaConsumerBackgroundService<TValue>> logger) : BackgroundService
{
    private readonly IKafkaConsumerService<TValue> _consumerService = consumerService ?? throw new ArgumentNullException(nameof(consumerService));
    private readonly ILogger<KafkaConsumerBackgroundService<TValue>> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.KafkaConsumerExecuteAsyncStartingInfo(typeof(TValue).Name);

        try
        {
            await _consumerService.ConsumeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.KafkaConsumerExecuteAsyncConsumeCanceledInfo(typeof(TValue).Name);
        }
        catch (Exception ex)
        {
            _logger.KafkaConsumerExecuteAsyncConsumeException(typeof(TValue).Name, ex);
            throw;
        }
        finally
        {
            _logger.KafkaConsumerExecuteAsyncStoppedInfo(typeof(TValue).Name);
        }
    }
}

public static partial class KafkaConsumerBackgroundServiceLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "KafkaConsumerExecuteAsync: starting `{ValueTypeName}`")]
    public static partial void KafkaConsumerExecuteAsyncStartingInfo(this ILogger logger, string valueTypeName);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Information,
        Message = "KafkaConsumerExecuteAsync: consume canceled `{ValueTypeName}`")]
    public static partial void KafkaConsumerExecuteAsyncConsumeCanceledInfo(this ILogger logger, string valueTypeName);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Error,
        Message = "KafkaConsumerExecuteAsync: consume exception `{ValueTypeName}`")]
    public static partial void KafkaConsumerExecuteAsyncConsumeException(this ILogger logger, string valueTypeName, Exception exception);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Information,
        Message = "KafkaConsumerExecuteAsync: stopped `{ValueTypeName}`")]
    public static partial void KafkaConsumerExecuteAsyncStoppedInfo(this ILogger logger, string valueTypeName);
}
