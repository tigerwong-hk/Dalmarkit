using Audit.Core;
using Audit.EntityFramework;
using Audit.EntityFramework.Providers;
using Audit.PostgreSql.Providers;
using Audit.WebApi;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Entities.Components;
using Dalmarkit.Common.Errors;

namespace Dalmarkit.AspNetCore.AuditTrail;

public class AuditTrailAuditDataProvider(string connectionString) : AuditDataProvider
{
    private const string DurationMsec = nameof(LogEntityBase.DurationMsec);
    private const string LogDetail = nameof(LogEntityBase.LogDetail);
    private const string Status = nameof(LogEntityBase.Status);
    private const string TraceId = nameof(LogEntityBase.TraceId);
    private const string UserId = nameof(LogEntityBase.UserId);

    private readonly PostgreSqlDataProvider ApiLogProvider = ConfigureApiLogProvider(connectionString);
    private readonly EntityFrameworkDataProvider AuditLogProvider = ConfigureAuditLogProvider();

    public override object InsertEvent(AuditEvent auditEvent)
    {
#pragma warning disable RCS1238 // Avoid nested ?: operators
        return auditEvent is AuditEventWebApi
            ? ApiLogProvider.InsertEvent(auditEvent)
            : auditEvent is AuditEventEntityFramework
            ? AuditLogProvider.InsertEvent(auditEvent)
            : throw new ArgumentException(ErrorMessages.ObjectInvalid, nameof(auditEvent));
#pragma warning restore RCS1238 // Avoid nested ?: operators
    }

    public override async Task<object> InsertEventAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
#pragma warning disable RCS1238 // Avoid nested ?: operators
        return auditEvent is AuditEventWebApi
            ? await ApiLogProvider.InsertEventAsync(auditEvent, cancellationToken)
            : auditEvent is AuditEventEntityFramework
            ? await AuditLogProvider.InsertEventAsync(auditEvent, cancellationToken)
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

        if (auditEvent is AuditEventEntityFramework)
        {
            AuditLogProvider.ReplaceEvent(eventId, auditEvent);
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

        if (auditEvent is AuditEventEntityFramework)
        {
            await AuditLogProvider.ReplaceEventAsync(eventId, auditEvent, cancellationToken);
            return;
        }

        throw new ArgumentException(ErrorMessages.ObjectInvalid, nameof(auditEvent));
    }

    private static PostgreSqlDataProvider ConfigureApiLogProvider(string connectionString)
    {
        return new(config => config
            .ConnectionString(connectionString)
            .TableName("ApiLogs")
            .Schema("public")
            .IdColumnName(nameof(ApiLog.Id))
            .DataColumn(LogDetail, Audit.PostgreSql.Configuration.DataType.JSONB)
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

    private static EntityFrameworkDataProvider ConfigureAuditLogProvider()
    {
        return new(config => config
            .AuditTypeMapper(_ => typeof(AuditLog))
            .AuditEntityAction<AuditLog>((ev, entry, entity) =>
            {
                EntityFrameworkEvent efEvent = ev.GetEntityFrameworkEvent();
                entity.Action = entry.Action;
                entity.AuditScopeId = Guid.TryParse(GetCustomFieldValueByKeyOrDbDefault(ev, nameof(AuditLog.AuditScopeId)).ToString(),
                    out Guid auditScopeId)
                        ? auditScopeId
                        : Guid.Empty;
                entity.ChangedValues = GetChangedValues(entry);
                entity.DurationMsec = ev.Duration;
                entity.Error = efEvent.ErrorMessage;
                entity.LogDetail = entry.ToJson();
                entity.PrimaryKey = entry.PrimaryKey.First().Value.ToString()!;
                entity.Status = efEvent.Success;
                entity.Table = entry.Table;
                entity.TraceId = GetCustomFieldValueByKeyOrDbDefault(ev, TraceId).ToString()!;
                entity.UserId = ev.Environment.UserName;
            })
            .IgnoreMatchedProperties(true)
        );
    }

    private static object GetCustomFieldValueByKeyOrDbDefault(AuditEvent auditEvent, string customValueKey)
    {
        return auditEvent.CustomFields.TryGetValue(customValueKey, out object? value)
            ? value ?? DBNull.Value
            : DBNull.Value;
    }

    private static string? GetChangedValues(EventEntry entry)
    {
        if (entry.Changes == null || entry.Changes.Count == 0)
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
