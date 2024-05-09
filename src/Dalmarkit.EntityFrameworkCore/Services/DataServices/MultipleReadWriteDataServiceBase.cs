using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Entities.DataModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public abstract class MultipleReadWriteDataServiceBase<TDbContext, TEntity>(TDbContext dbContext)
    : ReadWriteDataServiceBase<TDbContext, TEntity>(dbContext), IMultipleReadWriteDataServiceBase<TEntity>
    where TDbContext : DbContext
    where TEntity : ReadWriteEntityBase, IDataModelMultiple
{
    public override async ValueTask<EntityEntry<TEntity>> CreateAsync(TEntity entity,
        AuditDetail auditDetail,
        CancellationToken cancellationToken)
    {
        entity.EntityHash = EntityHasher.Hash(entity);

        return await base.CreateAsync(entity, auditDetail, cancellationToken);
    }
}
