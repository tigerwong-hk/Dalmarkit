using System.Linq.Expressions;
using System.Reflection;
using Audit.Core;
using Audit.EntityFramework;
using Audit.PostgreSql.Providers;
using Audit.WebApi;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Entities.Components;
using Dalmarkit.Common.Errors;
using Microsoft.EntityFrameworkCore;

namespace Dalmarkit.AspNetCore.AuditTrail;

/// <summary>
/// Routes Audit.NET events to their sink: <see cref="AuditEventWebApi"/> to <c>ApiLogs</c> (PostgreSQL provider) and
/// <see cref="AuditEventEntityFramework"/> to <c>AuditLogs</c>. A single <c>SaveChanges</c> raises one
/// <see cref="AuditEventEntityFramework"/> whose entries list every changed entity; this provider persists one
/// <c>AuditLog</c> row per entry - all correlated by a shared <see cref="AuditLog.AuditScopeId"/> - in a single
/// dedicated <typeparamref name="TDbContext"/> transaction, decoupled from the audited operation's transaction.
/// </summary>
/// <typeparam name="TDbContext">Application database context that owns the <c>AuditLogs</c> table.</typeparam>
/// <param name="connectionString">Connection string for the <c>ApiLogs</c> PostgreSQL provider.</param>
/// <param name="dbContextOptions">Options used to construct a dedicated context for writing <c>AuditLogs</c>.</param>
/// <param name="contextFactory">Optional factory that builds a fresh, disposable <typeparamref name="TDbContext"/> from
/// the supplied options. Supply it for contexts without a public <c>DbContextOptions</c> constructor, or for
/// trimming/NativeAOT. When <c>null</c>, a compiled factory over the context's <c>DbContextOptions</c> constructor is
/// used.</param>
public class AuditTrailAuditDataProvider<TDbContext>(
    string connectionString,
    DbContextOptions<TDbContext> dbContextOptions,
    Func<DbContextOptions<TDbContext>, TDbContext>? contextFactory) : AuditDataProvider
    where TDbContext : DbContext
{
    private const string DurationMsec = nameof(LogEntityBase.DurationMsec);
    private const string LogDetail = nameof(LogEntityBase.LogDetail);
    private const string Status = nameof(LogEntityBase.Status);
    private const string TraceId = nameof(LogEntityBase.TraceId);
    private const string UserId = nameof(LogEntityBase.UserId);

    private readonly PostgreSqlDataProvider ApiLogProvider = ConfigureApiLogProvider(connectionString);
    private readonly DbContextOptions<TDbContext> _dbContextOptions = dbContextOptions;
    private readonly Func<DbContextOptions<TDbContext>, TDbContext> _contextFactory = contextFactory ?? BuildDefaultContextFactory();

    /// <summary>
    /// Backward-compatible constructor that uses the default (reflection-compiled) context factory. Kept as an explicit
    /// overload — not an optional parameter on the primary constructor — so the original two-argument signature stays in
    /// metadata and already-compiled consumers do not break on upgrade.
    /// </summary>
    /// <param name="connectionString">Connection string for the <c>ApiLogs</c> PostgreSQL provider.</param>
    /// <param name="dbContextOptions">Options used to construct a dedicated context for writing <c>AuditLogs</c>.</param>
    public AuditTrailAuditDataProvider(string connectionString, DbContextOptions<TDbContext> dbContextOptions)
        : this(connectionString, dbContextOptions, null)
    {
    }

    public override object InsertEvent(AuditEvent auditEvent)
    {
#pragma warning disable RCS1238 // Avoid nested ?: operators
        return auditEvent is AuditEventWebApi
            ? ApiLogProvider.InsertEvent(auditEvent)
            : auditEvent is AuditEventEntityFramework entityFrameworkEvent
            ? InsertAuditLogs(entityFrameworkEvent)
            : throw new ArgumentException(ErrorMessages.ObjectInvalid, nameof(auditEvent));
#pragma warning restore RCS1238 // Avoid nested ?: operators
    }

    public override async Task<object> InsertEventAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
#pragma warning disable RCS1238 // Avoid nested ?: operators
        return auditEvent is AuditEventWebApi
            ? await ApiLogProvider.InsertEventAsync(auditEvent, cancellationToken)
            : auditEvent is AuditEventEntityFramework entityFrameworkEvent
            ? await InsertAuditLogsAsync(entityFrameworkEvent, cancellationToken)
            : throw new ArgumentException(ErrorMessages.ObjectInvalid, nameof(auditEvent));
#pragma warning restore RCS1238 // Avoid nested ?: operators
    }

    public override void ReplaceEvent(object eventId, AuditEvent auditEvent)
    {
        if (auditEvent is AuditEventWebApi)
        {
            ApiLogProvider.ReplaceEvent(eventId, auditEvent);
            return;
        }

        if (auditEvent is AuditEventEntityFramework entityFrameworkEvent)
        {
            ReplaceAuditLogs(eventId, entityFrameworkEvent);
            return;
        }

        throw new ArgumentException(ErrorMessages.ObjectInvalid, nameof(auditEvent));
    }

    public override async Task ReplaceEventAsync(object eventId, AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        if (auditEvent is AuditEventWebApi)
        {
            await ApiLogProvider.ReplaceEventAsync(eventId, auditEvent, cancellationToken);
            return;
        }

        if (auditEvent is AuditEventEntityFramework entityFrameworkEvent)
        {
            await ReplaceAuditLogsAsync(eventId, entityFrameworkEvent, cancellationToken);
            return;
        }

        throw new ArgumentException(ErrorMessages.ObjectInvalid, nameof(auditEvent));
    }

    // ---- AuditLogs: one row per EF entry, written in a single dedicated-context transaction ----

    private List<long> InsertAuditLogs(AuditEventEntityFramework auditEvent)
    {
        List<AuditLog> auditLogs = BuildAuditLogs(auditEvent, auditEvent.GetEntityFrameworkEvent());
        if (auditLogs.Count == 0)
        {
            return [];
        }

        using TDbContext dbContext = CreateAuditDbContext();
        dbContext.Set<AuditLog>().AddRange(auditLogs);
        _ = dbContext.SaveChanges();
        return auditLogs.ConvertAll(auditLog => auditLog.Id);
    }

    private async Task<List<long>> InsertAuditLogsAsync(AuditEventEntityFramework auditEvent, CancellationToken cancellationToken)
    {
        List<AuditLog> auditLogs = BuildAuditLogs(auditEvent, auditEvent.GetEntityFrameworkEvent());
        if (auditLogs.Count == 0)
        {
            return [];
        }

        await using TDbContext dbContext = CreateAuditDbContext();
        await dbContext.Set<AuditLog>().AddRangeAsync(auditLogs, cancellationToken);
        _ = await dbContext.SaveChangesAsync(cancellationToken);
        return auditLogs.ConvertAll(auditLog => auditLog.Id);
    }

    private void ReplaceAuditLogs(object eventId, AuditEventEntityFramework auditEvent)
    {
        if (eventId is not List<long> auditLogIds || auditLogIds.Count == 0)
        {
            return;
        }

        EntityFrameworkEvent? efEvent = auditEvent.GetEntityFrameworkEvent();
        List<EventEntry> entries = efEvent?.Entries?.ToList() ?? [];

        using TDbContext dbContext = CreateAuditDbContext();
        // AsTracking: the update below relies on change tracking, which a NoTracking context default would otherwise disable.
        List<AuditLog> auditLogs = [.. dbContext.Set<AuditLog>().AsTracking().Where(auditLog => auditLogIds.Contains(auditLog.Id))];
        if (RepopulateAuditLogs(auditLogs, auditLogIds, entries, auditEvent, efEvent))
        {
            _ = dbContext.SaveChanges();
        }
    }

    private async Task ReplaceAuditLogsAsync(object eventId, AuditEventEntityFramework auditEvent, CancellationToken cancellationToken)
    {
        if (eventId is not List<long> auditLogIds || auditLogIds.Count == 0)
        {
            return;
        }

        EntityFrameworkEvent? efEvent = auditEvent.GetEntityFrameworkEvent();
        List<EventEntry> entries = efEvent?.Entries?.ToList() ?? [];

        await using TDbContext dbContext = CreateAuditDbContext();
        // AsTracking: the update below relies on change tracking, which a NoTracking context default would otherwise disable.
        List<AuditLog> auditLogs = await dbContext.Set<AuditLog>()
            .AsTracking()
            .Where(auditLog => auditLogIds.Contains(auditLog.Id))
            .ToListAsync(cancellationToken);
        if (RepopulateAuditLogs(auditLogs, auditLogIds, entries, auditEvent, efEvent))
        {
            _ = await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static List<AuditLog> BuildAuditLogs(AuditEventEntityFramework auditEvent, EntityFrameworkEvent? efEvent)
    {
        // One AuditLog per changed entity. An empty list when the event has no auditable entries (e.g. a save that
        // touched only opted-out entities), so no empty row is written and the dedicated-context write cannot recurse.
        List<EventEntry> entries = efEvent?.Entries?.ToList() ?? [];
        List<AuditLog> auditLogs = new(entries.Count);
        foreach (EventEntry entry in entries)
        {
            AuditLog auditLog = new();
            PopulateAuditLog(auditLog, auditEvent, efEvent, entry);
            auditLogs.Add(auditLog);
        }

        return auditLogs;
    }

    private static bool RepopulateAuditLogs(
        List<AuditLog> auditLogs,
        List<long> auditLogIds,
        List<EventEntry> entries,
        AuditEventEntityFramework auditEvent,
        EntityFrameworkEvent? efEvent)
    {
        // Re-map persisted rows so final (post-SaveChanges) primary keys, success flag and error are captured. Rows
        // pair to entries positionally: insert returned ids in entry order and Entries is never mutated, so the order
        // is stable across the two phases. Returns whether any row was updated.
        bool updated = false;
        for (int index = 0; index < auditLogIds.Count; index++)
        {
            AuditLog? auditLog = auditLogs.Find(candidate => candidate.Id == auditLogIds[index]);
            if (auditLog is null)
            {
                continue;
            }

            EventEntry? entry = index < entries.Count ? entries[index] : null;
            PopulateAuditLog(auditLog, auditEvent, efEvent, entry);
            updated = true;
        }

        return updated;
    }

    private static void PopulateAuditLog(AuditLog auditLog, AuditEvent auditEvent, EntityFrameworkEvent? efEvent, EventEntry? entry)
    {
        auditLog.Action = entry?.Action ?? string.Empty;
        auditLog.AuditScopeId = Guid.TryParse(GetCustomFieldValueByKeyOrDbDefault(auditEvent, nameof(AuditLog.AuditScopeId)).ToString(),
            out Guid auditScopeId)
                ? auditScopeId
                : Guid.Empty;
        auditLog.ChangedValues = GetChangedValues(entry);
        auditLog.DurationMsec = auditEvent.Duration;
        auditLog.Error = efEvent?.ErrorMessage;
        auditLog.LogDetail = entry?.ToJson() ?? "{}";
        auditLog.PrimaryKey = GetPrimaryKey(entry);
        auditLog.Status = efEvent?.Success ?? false;
        auditLog.Table = entry?.Table ?? string.Empty;
        auditLog.TraceId = GetCustomFieldValueByKeyOrDbDefault(auditEvent, TraceId).ToString() ?? string.Empty;
        auditLog.UserId = auditEvent.Environment.UserName;
    }

    private TDbContext CreateAuditDbContext()
    {
        // Dedicated context for writing audit rows, with its own auditing disabled so persisting AuditLog rows cannot
        // recurse into the audit pipeline.
        TDbContext dbContext = _contextFactory(_dbContextOptions);
        if (dbContext is IAuditDbContext auditDbContext)
        {
            auditDbContext.AuditDisabled = true;
        }

        return dbContext;
    }

    private static Func<DbContextOptions<TDbContext>, TDbContext> BuildDefaultContextFactory()
    {
        // Resolve the standard EF DbContextOptions constructor and compile a delegate over it. Only invoked when the
        // caller supplied no explicit factory, so an AOT/trimming caller that passes one never reaches Expression.Compile.
        // A context without such a constructor yields a deferred-throwing factory with a clear "supply a factory" message.
        ConstructorInfo? constructor = typeof(TDbContext).GetConstructor([typeof(DbContextOptions<TDbContext>)])
            ?? typeof(TDbContext).GetConstructor([typeof(DbContextOptions)]);
        if (constructor is null)
        {
            return _ => throw new InvalidOperationException(
                $"{typeof(TDbContext).Name} has no public constructor accepting DbContextOptions<{typeof(TDbContext).Name}> or DbContextOptions. "
                + "Supply a context factory via the AddAuditTrail overload instead.");
        }

        ParameterExpression optionsParameter = Expression.Parameter(typeof(DbContextOptions<TDbContext>), "options");
        Expression constructorArgument = constructor.GetParameters()[0].ParameterType == typeof(DbContextOptions)
            ? Expression.Convert(optionsParameter, typeof(DbContextOptions))
            : optionsParameter;
        return Expression.Lambda<Func<DbContextOptions<TDbContext>, TDbContext>>(
            Expression.New(constructor, constructorArgument), optionsParameter).Compile();
    }

    private static string GetPrimaryKey(EventEntry? entry)
    {
        if (entry?.PrimaryKey is null || entry.PrimaryKey.Count == 0)
        {
            return string.Empty;
        }

        // Single-key entities keep the bare value (backward-compatible); composite keys are joined so no key part is lost.
        return entry.PrimaryKey.Count == 1
            ? entry.PrimaryKey.First().Value?.ToString() ?? string.Empty
            : string.Join(",", entry.PrimaryKey.Select(keyValue => $"{keyValue.Key}={keyValue.Value}"));
    }

    private static PostgreSqlDataProvider ConfigureApiLogProvider(string connectionString)
    {
        return new(config => config
            .ConnectionString(connectionString)
            .TableName("ApiLogs")
            .Schema("public")
            .IdColumnName(nameof(ApiLog.Id))
            .DataJsonColumn(LogDetail, Audit.PostgreSql.Configuration.DataType.JSONB)
            .LastUpdatedColumnName(nameof(LogEntityBase.ModifiedOn))
            .CustomColumn(DurationMsec, ev => ev.Duration)
            .CustomColumn(Status, ev => GetCustomFieldValueByKeyOrDbDefault(ev, Status))
            .CustomColumn(TraceId, ev => GetCustomFieldValueByKeyOrDbDefault(ev, TraceId))
            .CustomColumn(UserId, ev => ev.Environment.UserName ?? string.Empty)
            .CustomColumn(nameof(ApiLog.ActionName), ev => GetCustomFieldValueByKeyOrDbDefault(ev, nameof(ApiLog.ActionName)))
            .CustomColumn(nameof(ApiLog.EventType), ev => ev.EventType)
            .CustomColumn(nameof(ApiLog.ResponseStatusCode), ev => GetCustomFieldValueByKeyOrDbDefault(ev, nameof(ApiLog.ResponseStatusCode)))
            .CustomColumn(nameof(ApiLog.Url), ev => GetCustomFieldValueByKeyOrDbDefault(ev, nameof(ApiLog.Url)))
            .CustomColumn(nameof(ApiLog.UserIp), ev => GetCustomFieldValueByKeyOrDbDefault(ev, nameof(ApiLog.UserIp)))
        );
    }

    private static object GetCustomFieldValueByKeyOrDbDefault(AuditEvent auditEvent, string customValueKey)
    {
        return auditEvent.CustomFields.TryGetValue(customValueKey, out object? value)
            ? value ?? DBNull.Value
            : DBNull.Value;
    }

    private static string? GetChangedValues(EventEntry? entry)
    {
        if (entry?.Changes == null || entry.Changes.Count == 0)
        {
            return null;
        }

        List<EventEntryChange> changedList = [.. entry.Changes];
        entry.Changes = changedList;

        Dictionary<string, ChangedValue> changes = [];
        foreach (EventEntryChange change in changedList)
        {
            changes.Add(change.ColumnName, new ChangedValue(change.OriginalValue, change.NewValue));
        }

        return Audit.Core.Configuration.JsonAdapter.Serialize(changes);
    }
}
