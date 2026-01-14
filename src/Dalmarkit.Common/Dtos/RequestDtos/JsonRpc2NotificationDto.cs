namespace Dalmarkit.Common.Dtos.RequestDtos;

public class JsonRpc2NotificationDto<TParams>
{
    public string JsonRpc { get; set; } = "2.0";
    public string Method { get; set; } = string.Empty;
    public TParams? Params { get; set; }
}
