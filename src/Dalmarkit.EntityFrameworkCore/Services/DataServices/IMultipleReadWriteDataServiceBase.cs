using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Entities.DataModels;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IMultipleReadWriteDataServiceBase<TEntity> : IReadWriteDataServiceBase<TEntity>
    where TEntity : ReadWriteEntityBase, IDataModelMultiple;
