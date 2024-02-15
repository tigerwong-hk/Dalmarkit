using Audit.Core;
using Dalmarkit.Common.AuditTrail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dalmarkit.AspNetCore.AuditTrail;

public static class AuditLogServiceCollectionExtensions
{
    /// <summary>
    /// Add audit trail
    /// </summary>
    /// <typeparam name="TDbContext">database context</typeparam>
    /// <param name="services">service descriptors collection</param>
    /// <param name="connectionString">database connection string</param>
    public static IServiceCollection AddAuditTrail<TDbContext>(this IServiceCollection services, string connectionString)
        where TDbContext : DbContext
    {
        _ = Configuration.Setup()
            .UseCustomProvider(new AuditTrailAuditDataProvider(connectionString))
            .WithCreationPolicy(EventCreationPolicy.InsertOnStartReplaceOnEnd);

        _ = Audit.EntityFramework.Configuration.Setup()
            .ForContext<TDbContext>(config => config
                .ExcludeValidationResults()
                .AuditEventType("EF:{context}"))
            .UseOptOut()
                .Ignore<ApiLog>()
                .Ignore<AuditLog>();

        return services;
    }
}
