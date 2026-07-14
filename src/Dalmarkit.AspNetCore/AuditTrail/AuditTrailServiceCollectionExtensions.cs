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
    /// <param name="dbContextOptionsBuilder">action to configure DbContext options</param>
    public static IServiceCollection AddAuditTrail<TDbContext>(this IServiceCollection services, string connectionString, Action<DbContextOptionsBuilder<TDbContext>> dbContextOptionsBuilder)
        where TDbContext : DbContext
    {
        return services.AddAuditTrail(connectionString, dbContextOptionsBuilder, contextFactory: null);
    }

    /// <summary>
    /// Add audit trail, supplying an explicit factory that builds the dedicated context used to write <c>AuditLogs</c>.
    /// </summary>
    /// <typeparam name="TDbContext">database context</typeparam>
    /// <param name="services">service descriptors collection</param>
    /// <param name="connectionString">database connection string</param>
    /// <param name="dbContextOptionsBuilder">action to configure DbContext options</param>
    /// <param name="contextFactory">factory that returns a fresh, disposable context from the configured options
    /// (e.g. <c>options =&gt; new AppDbContext(options)</c>); supply for contexts without a public
    /// <c>DbContextOptions</c> constructor, or for trimming/NativeAOT. When <c>null</c>, a compiled factory over the
    /// context's <c>DbContextOptions</c> constructor is used.</param>
    public static IServiceCollection AddAuditTrail<TDbContext>(
        this IServiceCollection services,
        string connectionString,
        Action<DbContextOptionsBuilder<TDbContext>> dbContextOptionsBuilder,
        Func<DbContextOptions<TDbContext>, TDbContext>? contextFactory)
        where TDbContext : DbContext
    {
        DbContextOptionsBuilder<TDbContext> optionsBuilder = new();
        dbContextOptionsBuilder(optionsBuilder);

        _ = Configuration.Setup()
            .UseCustomProvider(new AuditTrailAuditDataProvider<TDbContext>(connectionString, optionsBuilder.Options, contextFactory))
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
