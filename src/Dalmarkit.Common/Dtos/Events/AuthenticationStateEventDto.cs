namespace Dalmarkit.Common.Dtos.Events;

public record AuthenticationStateEventDto(
    string AuthenticationState,
    DateTimeOffset EventTimestamp);
