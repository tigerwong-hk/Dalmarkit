using Dalmarkit.Common.Entities.DataModels;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IMultipleReadWriteDataServiceBase<TEntity> : IReadWriteDataServiceBase<TEntity>
    where TEntity : class, IDataModelMultipleReadWrite;
