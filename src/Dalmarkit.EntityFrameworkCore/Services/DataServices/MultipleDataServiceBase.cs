using Dalmarkit.Common.Entities;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.DataModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public abstract class MultipleDataServiceBase<TDbContext, TEntity>(TDbContext dbContext)
    : DataServiceBase<TDbContext, TEntity>(dbContext), IMultipleDataServiceBase<TEntity>
    where TDbContext : DbContext
    where TEntity : class, IDataModelMultiple, IDataModelBase
{
    public override async ValueTask<EntityEntry<TEntity>> CreateAsync(TEntity entity,
        AuditDetail auditDetail,
        CancellationToken cancellationToken)
    {
        entity.EntityHash = EntityHasher.Hash(entity);

        return await base.CreateAsync(entity, auditDetail, cancellationToken);
    }
}
