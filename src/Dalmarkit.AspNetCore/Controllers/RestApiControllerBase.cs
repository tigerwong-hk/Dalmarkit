using Dalmarkit.Common.Api.Responses;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Identity;
using Dalmarkit.Common.Validation;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Dalmarkit.AspNetCore.Controllers;

[ApiController]
[Route("api/v1/[controller]/[action]")]
public abstract class RestApiControllerBase : ControllerBase
{
    protected virtual JsonResult ApiResponse<TValue>(Result<TValue, ErrorDetail> result)
    {
        _ = Guard.NotNull(result, nameof(result));

#pragma warning disable RCS1238 // Avoid nested ?: operators
        return result.HasError
            ? Failure(result.Error!)
            : result.HasValue
                ? Success(result.Value!)
                : Success();
#pragma warning restore RCS1238 // Avoid nested ?: operators
    }

    protected virtual JsonResult ApiResponse<TValue>(Result<TValue, ErrorDetail> result, JsonSerializerOptions options)
    {
        _ = Guard.NotNull(result, nameof(result));
        _ = Guard.NotNull(options, nameof(options));

#pragma warning disable RCS1238 // Avoid nested ?: operators
        return result.HasError
            ? Failure(result.Error!, options)
            : result.HasValue
                ? Success(result.Value!, options)
                : Success(options);
#pragma warning restore RCS1238 // Avoid nested ?: operators
    }

    protected virtual AuditDetail CreateAuditDetail()
    {
        _ = Guard.NotNull(User.Identity, nameof(User.Identity));

        string userId = User.Identity!.GetAccountId();
        _ = Guard.NotNullOrWhiteSpace(userId, nameof(userId));

        string clientId = User.Identity!.GetClientId();
        _ = Guard.NotNullOrWhiteSpace(clientId, nameof(clientId));

        return new AuditDetail(clientId, userId, DateTime.UtcNow);
    }

    private static JsonResult Failure(ErrorDetail errorDetail)
    {
        return new(
        new DefaultResponse
        {
            ErrorCode = errorDetail.Code,
            ErrorMessage = errorDetail.Message
        });
    }

    private static JsonResult Failure(ErrorDetail errorDetail, JsonSerializerOptions options)
    {
        return new(
        new DefaultResponse
        {
            ErrorCode = errorDetail.Code,
            ErrorMessage = errorDetail.Message
        },
        options);
    }

    private static JsonResult Success()
    {
        return new(new DefaultResponse { Success = true });
    }

    private static JsonResult Success(JsonSerializerOptions options)
    {
        return new(new DefaultResponse { Success = true }, options);
    }

    private static JsonResult Success<T>(T data)
    {
        return new(new CommonApiResponse<T> { Success = true, Data = data });
    }

    private static JsonResult Success<T>(T data, JsonSerializerOptions options)
    {
        return new(new CommonApiResponse<T> { Success = true, Data = data }, options);
    }
}
