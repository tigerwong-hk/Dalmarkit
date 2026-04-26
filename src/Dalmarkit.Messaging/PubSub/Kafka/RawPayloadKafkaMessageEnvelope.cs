using Confluent.Kafka;
using Dalmarkit.Messaging.Kafka.Producers;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text;

namespace Dalmarkit.Messaging.PubSub.Kafka;

/// <summary>
/// Writes the payload as the Kafka message value with no envelope. <c>method</c> and <c>publishTimestamp</c>
/// are surfaced as Kafka message headers so consumers can still discriminate on method and detect staleness
/// Resolves a typed <see cref="IKafkaProducerService{TPayload}"/> per call so each gets its own underlying Confluent producer (registered via open-generic singleton)
/// </summary>
/// <param name="serviceProvider">Service provider</param>
public sealed class RawPayloadKafkaMessageEnvelope(IServiceProvider serviceProvider) : IKafkaMessageEnvelope
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public async Task<DeliveryResult<string, TPayload>> PublishAsync<TPayload>(
        string topic,
        string method,
        TPayload payload,
        string? key,
        CancellationToken cancellationToken)
    {
        IKafkaProducerService<TPayload> producer = _serviceProvider.GetRequiredService<IKafkaProducerService<TPayload>>();

        Headers headers = new()
        {
            { KafkaMessageHeaders.Method, Encoding.UTF8.GetBytes(method) },
            {
                KafkaMessageHeaders.PublishTimestamp,
                Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            }
        };

        Message<string, TPayload> message = new()
        {
            // Null intentional: defers partitioning to librdkafka's configured partitioner
            Key = key!,
            Value = payload,
            Headers = headers,
        };

        return await producer.ProduceAsync(topic, message, cancellationToken).ConfigureAwait(false);
    }
}
