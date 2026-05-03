namespace Dalmarkit.Messaging.Kafka.Consumers;

public readonly record struct MessageIdentity(
    string Topic,
    int Partition,
    long Offset,
    string? BusinessMessageId = default)
{
    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(BusinessMessageId)
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Topic}:{Partition}:{Offset}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Topic}:{Partition}:{Offset}:{BusinessMessageId}");
    }
}
