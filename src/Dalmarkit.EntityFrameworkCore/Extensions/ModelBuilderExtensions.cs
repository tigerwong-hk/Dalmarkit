using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.BaseEntities;
using Microsoft.EntityFrameworkCore;

namespace Dalmarkit.EntityFrameworkCore.Extensions;

public static class ModelBuilderExtensions
{
    public const string DefaultDateTimeValueSql = "now() at time zone 'utc'";
    public const string DefaultGuidValueSql = "gen_random_uuid()";

    public static ModelBuilder BuildApiLogEntity(this ModelBuilder modelBuilder)
    {
        _ = modelBuilder.BuildLogEntity<ApiLog>();

        _ = modelBuilder.Entity<ApiLog>().HasIndex(l => l.ActionName);
        _ = modelBuilder.Entity<ApiLog>().HasIndex(l => l.EventType);
        _ = modelBuilder.Entity<ApiLog>().HasIndex(l => l.ResponseStatusCode);
        _ = modelBuilder.Entity<ApiLog>().HasIndex(l => l.Url);
        _ = modelBuilder.Entity<ApiLog>().HasIndex(l => l.UserIp);

        return modelBuilder;
    }

    public static ModelBuilder BuildAuditLogEntity(this ModelBuilder modelBuilder)
    {
        _ = modelBuilder.BuildLogEntity<AuditLog>();

        _ = modelBuilder.Entity<AuditLog>().HasIndex(l => l.ChangedValues).HasMethod("gin");
        _ = modelBuilder.Entity<AuditLog>().HasIndex(l => l.PrimaryKey);
        _ = modelBuilder.Entity<AuditLog>().HasIndex(l => new { l.Table, l.PrimaryKey });

        return modelBuilder;
    }

    public static ModelBuilder BuildLogEntity<T>(this ModelBuilder modelBuilder)
        where T : LogEntityBase
    {
        _ = modelBuilder.Entity<T>().Property(t => t.CreatedOn).HasDefaultValueSql(DefaultDateTimeValueSql);
        _ = modelBuilder.Entity<T>().HasIndex(t => t.CreatedOn);

        _ = modelBuilder.Entity<T>().Property(t => t.ModifiedOn).HasDefaultValueSql(DefaultDateTimeValueSql);
        _ = modelBuilder.Entity<T>().HasIndex(t => t.ModifiedOn);

        _ = modelBuilder.Entity<T>().HasIndex(l => l.TraceId);
        _ = modelBuilder.Entity<T>().HasIndex(l => l.UserId);

        return modelBuilder;
    }

    public static ModelBuilder BuildMultipleReadOnlyEntity<T>(this ModelBuilder modelBuilder)
        where T : MultipleReadOnlyEntityBase
    {
        _ = modelBuilder.BuildBasicEntity<T>();

        _ = modelBuilder.Entity<T>()
            .HasIndex(e => new { e.CreateRequestId, e.ClientId, e.EntityHash })
            .IsUnique();

        return modelBuilder;
    }

    public static ModelBuilder BuildMultipleReadWriteEntity<T>(this ModelBuilder modelBuilder)
        where T : MultipleReadWriteEntityBase
    {
        _ = modelBuilder.BuildBasicReadWriteEntity<T>();

        _ = modelBuilder.Entity<T>()
            .HasIndex(e => new { e.CreateRequestId, e.ClientId, e.EntityHash })
            .IsUnique();

        return modelBuilder;
    }

    public static ModelBuilder BuildReadOnlyEntity<T>(this ModelBuilder modelBuilder)
        where T : ReadOnlyEntityBase
    {
        _ = modelBuilder.BuildBasicEntity<T>();

        _ = modelBuilder.Entity<T>().HasIndex(e => new { e.CreateRequestId, e.ClientId }).IsUnique();

        return modelBuilder;
    }

    public static ModelBuilder BuildReadWriteEntity<T>(this ModelBuilder modelBuilder)
        where T : ReadWriteEntityBase
    {
        _ = modelBuilder.BuildBasicReadWriteEntity<T>();

        _ = modelBuilder.Entity<T>().HasIndex(e => new { e.CreateRequestId, e.ClientId }).IsUnique();

        return modelBuilder;
    }

    private static ModelBuilder BuildBasicEntity<T>(this ModelBuilder modelBuilder)
        where T : EntityBase
    {
        _ = modelBuilder.Entity<T>().HasIndex(t => t.ClientId);

        _ = modelBuilder.Entity<T>().Property(t => t.CreatedOn).HasDefaultValueSql(DefaultDateTimeValueSql);
        _ = modelBuilder.Entity<T>().HasIndex(t => t.CreatedOn);

        _ = modelBuilder.Entity<T>().HasIndex(t => t.CreatorId);

        return modelBuilder;
    }

    private static ModelBuilder BuildBasicReadWriteEntity<T>(this ModelBuilder modelBuilder)
        where T : ReadWriteEntityBase
    {
        _ = modelBuilder.BuildBasicEntity<T>();

        _ = modelBuilder.Entity<T>().Property(t => t.ModifiedOn).HasDefaultValueSql(DefaultDateTimeValueSql);
        _ = modelBuilder.Entity<T>().HasIndex(t => t.ModifiedOn);

        _ = modelBuilder.Entity<T>().HasIndex(t => t.ModifierId);

        return modelBuilder;
    }
}
