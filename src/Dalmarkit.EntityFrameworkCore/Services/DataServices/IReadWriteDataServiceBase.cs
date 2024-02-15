using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Entities.DataModels;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IReadWriteDataServiceBase<TEntity> : IDataServiceBase<TEntity>
    where TEntity : class, IDataModelReadWrite
{
    EntityEntry<TEntity> Update(TEntity entity, AuditDetail auditDetail);
}
