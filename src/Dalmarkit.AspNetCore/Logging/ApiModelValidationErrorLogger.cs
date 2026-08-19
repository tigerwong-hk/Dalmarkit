using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dalmarkit.AspNetCore.Logging;

public static class ApiModelValidationErrorLogger
{
    private const string Redaction = "[Redacted]";

    public static void LogInformation(ActionContext context)
    {
        ILoggerFactory loggerFactory = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger(GetLoggerCategoryName(context.ActionDescriptor));

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
    /// Category name for this action's logger, shaped so a Serilog <c>MinimumLevel:Override</c> key matches
    /// it at NAMESPACE, CONTROLLER and ACTION granularity - which is what lets an operator silence this one
    /// line for a PII-bearing action without touching InvalidModelStateResponseFactory. The line is caller
    /// data even after redaction (field names, and any value a custom message embeds unquoted), so
    /// per-action suppression has to be reachable from configuration.
    /// </para>
    /// <para>
    /// ActionDescriptor.DisplayName CANNOT be filtered per action, which is why this method exists. MVC
    /// renders it as "Ns.PatientsController.CreateAsync (AssemblyName)", and Serilog matches an override
    /// only on the whole context or on a DOT boundary - so the trailing " (AssemblyName)" makes
    /// "Ns.PatientsController.CreateAsync" a NON-match. The override is accepted in silence and the line
    /// still logs, which is the worst failure mode available. The key that does work has to spell the
    /// assembly name, so renaming the host project breaks it just as silently.
    /// </para>
    /// <para>
    /// ActionName, not MethodInfo.Name: it is the name MVC itself reports - the Async suffix stripped when
    /// SuppressAsyncSuffixInActionNames is true (the default), and an [ActionName] override honoured - so
    /// one key shape matches what the rest of MVC calls this action, AuditApiAction.ActionName included.
    /// </para>
    /// <para>
    /// The controller TYPE name, not ControllerName ("Patients"), keeps the category namespace-qualified: it
    /// nests under the same prefixes as every ILogger&lt;T&gt; in the app, so "Ns" or "Ns.PatientsController"
    /// filters a whole area, while "Ns.PatientsController.Create" hits ONLY this line and leaves the
    /// controller's own ILogger&lt;PatientsController&gt; (category "Ns.PatientsController", not under
    /// "Ns.PatientsController.") logging normally.
    /// </para>
    /// </summary>
    /// <param name="actionDescriptor">action descriptor</param>
    /// <returns>logger category name</returns>
    public static string GetLoggerCategoryName(ActionDescriptor actionDescriptor)
    {
        ArgumentNullException.ThrowIfNull(actionDescriptor);

        // FullName is null for an open generic type parameter, so pattern-match it rather than assuming it.
        // ControllerTypeInfo is declared non-nullable but is settable, so a hand-built descriptor (a test)
        // can leave it null - hence the null-conditional.
        return actionDescriptor is ControllerActionDescriptor controllerActionDescriptor
            && controllerActionDescriptor.ControllerTypeInfo?.FullName is string controllerTypeName
            && !string.IsNullOrWhiteSpace(controllerActionDescriptor.ActionName)
                ? $"{controllerTypeName}.{controllerActionDescriptor.ActionName}"
                : GetFallbackLoggerCategoryName(actionDescriptor);
    }

    /// <summary>
    /// Not a controller action: a Razor Page's DisplayName is a route ("/Patients/Create"), filterable only
    /// as a whole, and Id is a GUID - un-filterable, but unique, which still beats an empty category. This is
    /// the 0.9.12 behavior, kept unchanged for everything that is not a ControllerActionDescriptor.
    /// </summary>
    /// <param name="actionDescriptor">action descriptor</param>
    /// <returns>logger category name</returns>
    private static string GetFallbackLoggerCategoryName(ActionDescriptor actionDescriptor)
    {
        return string.IsNullOrWhiteSpace(actionDescriptor.DisplayName) ? actionDescriptor.Id : actionDescriptor.DisplayName;
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

public static partial class ApiModelValidationErrorLoggerLogs
{
    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Information,
        Message = "Model validation error(s) at `{RequestUrl}`: {ErrorMessages}")]
    public static partial void ModelValidationErrorsAt(
        this ILogger logger, string requestUrl, string? errorMessages);
}
