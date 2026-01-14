namespace Dalmarkit.Common.Dtos.ResponseDtos;

public class JsonRpc2ResponseDto<TResult, TErrorData>
{
    public string Jsonrpc { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public TResult? Result { get; set; }
    public JsonRpc2ResponseError<TErrorData>? Error { get; set; }
}

public static class JsonRpc2ResponseErrorCode
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

public class JsonRpc2ResponseError<TData>
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public TData? Data { get; set; }
}
