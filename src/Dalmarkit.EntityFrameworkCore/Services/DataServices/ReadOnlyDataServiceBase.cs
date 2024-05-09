using Dalmarkit.Common.Entities.BaseEntities;
using Microsoft.EntityFrameworkCore;

namespace Dalmarkit.EntityFrameworkCore.Services.DataServices;

public abstract class ReadOnlyDataServiceBase<TDbContext, TEntity>(TDbContext dbContext)
    : DataServiceBase<TDbContext, TEntity>(dbContext), IReadOnlyDataServiceBase<TEntity>
    where TDbContext : DbContext
    where TEntity : ReadOnlyEntityBase;
