namespace Dalmarkit.Common.Api.Responses;

public class Result<TValue, TError>
{
    public TError? Error { get; internal set; }
    public bool HasError { get; internal set; }
    public bool HasValue { get; internal set; }
    public TValue? Value { get; internal set; }
}
