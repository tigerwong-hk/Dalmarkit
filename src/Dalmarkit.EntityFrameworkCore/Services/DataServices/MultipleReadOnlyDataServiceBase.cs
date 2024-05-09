using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Entities.DataModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public abstract class MultipleReadOnlyDataServiceBase<TDbContext, TEntity>(TDbContext dbContext)
    : ReadOnlyDataServiceBase<TDbContext, TEntity>(dbContext), IMultipleReadOnlyDataServiceBase<TEntity>
    where TDbContext : DbContext
    where TEntity : ReadOnlyEntityBase, IDataModelMultiple
{
    public override async ValueTask<EntityEntry<TEntity>> CreateAsync(TEntity entity,
        AuditDetail auditDetail,
        CancellationToken cancellationToken)
    {
        entity.EntityHash = EntityHasher.Hash(entity);

        return await base.CreateAsync(entity, auditDetail, cancellationToken);
    }
}
