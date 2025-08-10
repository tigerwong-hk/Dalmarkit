using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public abstract class DataServiceBase<TDbContext, TEntity>(TDbContext dbContext) : IDataServiceBase<TEntity>
    where TDbContext : DbContext
    where TEntity : EntityBase
{
    protected TDbContext DbContext { get; } = Guard.NotNull(dbContext, nameof(dbContext));

    public virtual async ValueTask<EntityEntry<TEntity>> CreateAsync(TEntity entity,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default)
    {
        entity.AppClientId = auditDetail.AppClientId;
        entity.CreatedOn = auditDetail.ModifiedOn;
        entity.CreatorId = auditDetail.ModifierId;

        return await DbContext.AddAsync(entity, cancellationToken);
    }

    public virtual async ValueTask<TEntity?> FindEntityIdAsync(Guid entityId, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        _ = Guard.NotEmpty(entityId, nameof(entityId));

        return await DbContext.FindAsync<TEntity>(entityId, cancellationToken);
    }

    public virtual async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await DbContext.SaveChangesAsync(cancellationToken);
    }
}
