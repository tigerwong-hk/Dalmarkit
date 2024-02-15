using Audit.WebApi;
using Microsoft.AspNetCore.Mvc;

namespace Dalmarkit.AspNetCore.AuditTrail;

public static class AuditTrailMvcOptionsExtensions
{
    /// <summary>
    /// Add global audit filter to MVC pipeline
    /// </summary>
    /// <param name="mvcOptions">programmatic configuration for the MVC framework</param>
    /// <param name="excludedActionNames">action names to exclude from audit trail</param>
    public static MvcOptions AddGlobalAuditFilter(this MvcOptions mvcOptions, IEnumerable<string> excludedActionNames)
    {
        mvcOptions.AddAuditFilter(config => config
            .LogActionIf(x => !excludedActionNames.Contains(x.ActionName))
            .WithEventType("MVC:{verb}:{controller}:{action}")
            .IncludeRequestBody(ctx => string.IsNullOrWhiteSpace(ctx.HttpContext.Request.ContentType) || !ctx.HttpContext.Request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            .IncludeModelState()
            .IncludeResponseBody());

        return mvcOptions;
    }
}
