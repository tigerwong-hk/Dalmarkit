using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Api.Responses;

public class DefaultResponse
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    public bool Success { get; set; }
}
