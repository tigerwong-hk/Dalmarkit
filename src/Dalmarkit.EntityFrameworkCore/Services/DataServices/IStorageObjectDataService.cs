using System.Linq.Expressions;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public interface IStorageObjectDataService<TEntity>
{
    Task<TEntity?> GetEntityByExpressionAsync(Expression<Func<TEntity, bool>> expression, CancellationToken cancellationToken = default);
}
