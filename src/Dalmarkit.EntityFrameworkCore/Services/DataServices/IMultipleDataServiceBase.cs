using Dalmarkit.Common.Entities.DataModels;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IMultipleDataServiceBase<TEntity> : IDataServiceBase<TEntity>
    where TEntity : class, IDataModelMultiple, IDataModelBase;
