using Dalmarkit.Common.Entities.BaseEntities;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IReadOnlyDataServiceBase<TEntity> : IDataServiceBase<TEntity>
    where TEntity : ReadOnlyEntityBase;
