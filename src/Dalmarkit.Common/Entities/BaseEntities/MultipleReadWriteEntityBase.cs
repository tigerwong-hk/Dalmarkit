using Dalmarkit.Common.Entities.DataModels;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Entities.BaseEntities;

public abstract class MultipleReadWriteEntityBase : ReadWriteEntityBase, IDataModelMultiple
{
    [Required]
    public string EntityHash { get; set; } = null!;
}
