using Audit.Core;
using Audit.EntityFramework;
using Audit.WebApi;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace Dalmarkit.AspNetCore.AuditTrail;

public static class AuditTrailApplicationBuilderExtensions
{
    public static void AddAuditTrailCustomActions(this IApplicationBuilder _, IHttpContextAccessor httpContextAccessor)
    {
        Audit.Core.Configuration.AddCustomAction(ActionType.OnScopeCreated, scope =>
        {
            if (scope.Event is AuditEventEntityFramework)
            {
                scope.Event.CustomFields[nameof(AuditLog.AuditScopeId)] = Guid.NewGuid();
            }
        });

        Audit.Core.Configuration.AddCustomAction(ActionType.OnEventSaving, scope =>
        {
            HttpContext? httpContext = httpContextAccessor.HttpContext;
            string? accountId = httpContext?.User?.Identity?.GetAccountId();
            scope.Event.Environment.UserName = string.IsNullOrEmpty(accountId) ? null : accountId;
            scope.Event.CustomFields[nameof(LogEntityBase.TraceId)] = httpContext?.TraceIdentifier;

            if (scope.Event is AuditEventWebApi)
            {
                AuditApiAction auditAction = scope.Event.GetWebApiAuditAction();

                // Audit.WebApi records 500 whenever the action threw, and a client abort throws
                // OperationCanceledException - so an abandoned request is currently audited as a server
                // fault. The caller left; match the 499 ExceptionsMiddleware sets and Kestrel logs.
                //
                // Assigned BEFORE the reads below on purpose: the ApiLogs column, the Status flag and the
                // LogDetail JSONB copy of the action all derive from this one property, so one assignment
                // fixes all three and nothing else in this method changes.
                if (httpContext?.RequestAborted.IsCancellationRequested == true)
                {
                    auditAction.ResponseStatusCode = StatusCodes.Status499ClientClosedRequest;
                    auditAction.ResponseStatus = "Client Closed Request";
                }

                scope.Event.CustomFields[nameof(ApiLog.ActionName)] = auditAction.ActionName;
                scope.Event.CustomFields[nameof(ApiLog.ResponseStatusCode)] = auditAction.ResponseStatusCode;
                scope.Event.CustomFields[nameof(LogEntityBase.Status)] = auditAction.ResponseStatusCode is >= (int)HttpStatusCode.OK and < (int)HttpStatusCode.Ambiguous;
                scope.Event.CustomFields[nameof(ApiLog.Url)] = auditAction.RequestUrl;
                scope.Event.CustomFields[nameof(ApiLog.UserIp)] = auditAction.IpAddress;
            }
        });
    }
}
