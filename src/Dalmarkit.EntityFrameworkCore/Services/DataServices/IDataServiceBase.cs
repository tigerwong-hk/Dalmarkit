using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.DataModels;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IDataServiceBase<TEntity>
    where TEntity : class, IDataModelBase
{
    ValueTask<EntityEntry<TEntity>> CreateAsync(TEntity entity,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default);

    ValueTask<TEntity?> FindEntityIdAsync(Guid entityId, bool includeDeleted = false, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
