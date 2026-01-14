namespace Dalmarkit.Common.Dtos.RequestDtos;

public class JsonRpc2RequestDto<TParams> : JsonRpc2NotificationDto<TParams>
{
    public string Id { get; set; } = string.Empty;
}
