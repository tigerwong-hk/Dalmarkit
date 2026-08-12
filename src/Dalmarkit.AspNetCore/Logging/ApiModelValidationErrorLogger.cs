using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dalmarkit.AspNetCore.Logging;

public static class ApiModelValidationErrorLogger
{
    private const string Redaction = "[Redacted]";

    public static void LogInformation(ActionContext context)
    {
        ILoggerFactory loggerFactory = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
        string loggerCategoryName = string.IsNullOrWhiteSpace(context.ActionDescriptor.DisplayName) ? context.ActionDescriptor.Id : context.ActionDescriptor.DisplayName;
        ILogger logger = loggerFactory.CreateLogger(loggerCategoryName);

        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        HttpRequest request = context.HttpContext.Request;

        // Path only. A query string is caller-supplied data and this line is durable application log, so
        // `?ssn=078-05-1120` must not reach it. The marker distinguishes "withheld" from "absent" for
        // whoever reads the log; the PATH is deliberately left intact - it identifies the resource, and no
        // string edit here can tell an id from an email, so a PII-bearing route segment is out of scope
        // (see "Known residuals" in the PR description).
        string requestUrl = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, request.Path)
            + (request.QueryString.HasValue ? "?" + Redaction : string.Empty);

        // Joining an empty sequence already yields "", so the old .Any() ternary is redundant.
        string errorMessages = string.Join(" | ", context.ModelState
            .SelectMany(entry => entry.Value == null ? [] : entry.Value.Errors
                .Select(error => RedactAttemptedValue(error.ErrorMessage, entry.Value.AttemptedValue))));

        logger.ModelValidationErrorsAt(requestUrl, errorMessages);
    }

    /// <summary>
    /// <para>
    /// A value-provider binding failure QUOTES THE REJECTED VALUE in its message ("The value
    /// '078-05-1120' is not valid."), so the message is caller data, not a constant. AttemptedValue is
    /// populated for query/route/form binding and null for a [FromBody] body, so this touches exactly the
    /// messages that can carry a value and leaves the JSON-path messages - the diagnostic ones - alone.
    /// </para>
    /// <para>
    /// MATCH THE QUOTED FORM, NOT THE BARE VALUE. All four value-bearing message providers wrap it in
    /// single quotes, and a DataAnnotations failure on the same field carries an AttemptedValue too while
    /// its message contains no value at all - so a bare Replace() of a short value shreds an innocent
    /// message: "must be between 2 and 20." with AttemptedValue "0" becomes "...between 2 and 2[Redacted]."
    /// Quoting also preserves the FIELD NAME in "The value '{0}' is not valid for {1}."
    /// </para>
    /// <para>
    /// Scoped per entry on purpose: only THIS entry's attempted value is removed from ITS OWN messages, so
    /// one field's value can never rewrite another field's message.
    /// </para>
    /// <para>
    /// The empty check is not about Replace() throwing (oldValue here is at minimum "''", never empty): an
    /// empty attempted value renders literally as "The value '' is not valid.", which leaks nothing, and
    /// rewriting it to "[Redacted]" would falsely imply a value was supplied.
    /// </para>
    /// </summary>
    /// <param name="errorMessage">error message</param>
    /// <param name="attemptedValue">attempted value</param>
    /// <returns>redacted error message</returns>
    private static string RedactAttemptedValue(string errorMessage, string? attemptedValue)
    {
        return string.IsNullOrEmpty(attemptedValue) || string.IsNullOrEmpty(errorMessage)
                ? errorMessage
                : errorMessage.Replace($"'{attemptedValue}'", $"'{Redaction}'", StringComparison.Ordinal);
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
