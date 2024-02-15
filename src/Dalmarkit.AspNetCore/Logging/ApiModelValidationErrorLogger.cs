using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dalmarkit.AspNetCore.Logging;

public static class ApiModelValidationErrorLogger
{
    public static void LogInformation(ActionContext context)
    {
        ILoggerFactory loggerFactory = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
        string loggerCategoryName = string.IsNullOrWhiteSpace(context.ActionDescriptor.DisplayName) ? context.ActionDescriptor.Id : context.ActionDescriptor.DisplayName;
        ILogger logger = loggerFactory.CreateLogger(loggerCategoryName);

        // Get error messages
        string errorMessages = context.ModelState.Values.Any()
            ? string.Join(" | ", context.ModelState.Values
                .SelectMany(x => x.Errors)
                .Select(x => x.ErrorMessage))
            : string.Empty;

        logger.ModelValidationErrorsAt(context.HttpContext.Request.GetDisplayUrl(), errorMessages);
    }
}

public static partial class ApiModelValidationErrorsLoggerLogs
{
    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Information,
        Message = "Model validation error(s) at `{RequestUrl}`: {ErrorMessages}")]
    public static partial void ModelValidationErrorsAt(
        this ILogger logger, string requestUrl, string? errorMessages);
}
