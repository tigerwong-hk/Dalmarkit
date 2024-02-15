namespace Dalmarkit.Common.Api.Responses;

public static class Result
{
    public static Result<TValue, TError> Error<TValue, TError>(TError error)
    {
        return new()
        {
            Error = error,
            HasError = true
        };
    }

    public static Result<TValue, TError> Ok<TValue, TError>()
    {
        return new()
        {
            Value = default,
            HasValue = false,
            HasError = false
        };
    }

    public static Result<TValue, TError> Ok<TValue, TError>(TValue value)
    {
        return new()
        {
            Value = value,
            HasValue = true,
            HasError = false
        };
    }
}
