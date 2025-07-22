using Dalmarkit.Common.Api.Responses;
using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Validation;

namespace Dalmarkit.EntityFrameworkCore.Services.ApplicationServices;

public abstract class ApplicationServiceBase()
{
    protected static Result<T, ErrorDetail> Error<T>(ErrorDetail errorDetailTemplate, params object[] args)
    {
        _ = Guard.NotNull(errorDetailTemplate, nameof(errorDetailTemplate));

        ErrorDetail errorDetail = errorDetailTemplate.WithArgs(args);

        return Result.Error<T, ErrorDetail>(errorDetail);
    }

    protected static Result<T, ErrorDetail> Ok<T>()
    {
        return Result.Ok<T, ErrorDetail>();
    }

    protected static Result<T, ErrorDetail> Ok<T>(T data)
    {
        return Result.Ok<T, ErrorDetail>(data);
    }
}
