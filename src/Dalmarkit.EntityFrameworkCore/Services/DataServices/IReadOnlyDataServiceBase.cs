using Dalmarkit.Common.Entities.DataModels;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IReadOnlyDataServiceBase<TEntity> : IDataServiceBase<TEntity>
    where TEntity : class, IDataModelReadOnly;
