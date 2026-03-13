namespace Dalmarkit.Common.Dtos.Events;

public record ConnectionStateEventDto(
    string ConnectionState,
    DateTimeOffset EventTimestamp);
