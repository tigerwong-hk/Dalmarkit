using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Api.Responses;

public class CommonApiResponse<T> : DefaultResponse
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public T Data { get; set; } = default!;
}
