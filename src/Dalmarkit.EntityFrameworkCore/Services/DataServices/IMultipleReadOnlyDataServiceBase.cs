using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Entities.DataModels;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IMultipleReadOnlyDataServiceBase<TEntity> : IReadOnlyDataServiceBase<TEntity>
    where TEntity : ReadOnlyEntityBase, IDataModelMultiple;
