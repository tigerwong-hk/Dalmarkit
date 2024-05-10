using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.BaseEntities;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IReadWriteDataServiceBase<TEntity> : IReadOnlyDataServiceBase<TEntity>
    where TEntity : ReadWriteEntityBase
{
    EntityEntry<TEntity> Update(TEntity entity, AuditDetail auditDetail);
}
