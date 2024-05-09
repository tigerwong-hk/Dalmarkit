using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.BaseEntities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public abstract class ReadWriteDataServiceBase<TDbContext, TEntity>(TDbContext dbContext)
    : ReadOnlyDataServiceBase<TDbContext, TEntity>(dbContext), IReadWriteDataServiceBase<TEntity>
    where TDbContext : DbContext
    where TEntity : ReadWriteEntityBase
{
    public override async ValueTask<EntityEntry<TEntity>> CreateAsync(TEntity entity,
        AuditDetail auditDetail,
        CancellationToken cancellationToken)
    {
        entity.ModifiedOn = auditDetail.ModifiedOn;
        entity.ModifierId = auditDetail.ModifierId;

        return await base.CreateAsync(entity, auditDetail, cancellationToken);
    }

    public override async ValueTask<TEntity?> FindEntityIdAsync(Guid entityId, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        TEntity? entity = await base.FindEntityIdAsync(entityId, includeDeleted, cancellationToken);

        return !includeDeleted && entity?.IsDeleted == true ? null : entity;
    }

    public virtual EntityEntry<TEntity> Update(TEntity entity, AuditDetail auditDetail)
    {
        entity.ModifiedOn = auditDetail.ModifiedOn;
        entity.ModifierId = auditDetail.ModifierId;

        return DbContext.Update(entity);
    }
}
