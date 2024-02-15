using Dalmarkit.Common.Entities.DataModels;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Entities.BaseEntities;

public abstract class ReadWriteEntityBase : EntityBase, IDataModelReadWrite
{
    public bool IsDeleted { get; set; }

    [ConcurrencyCheck]
    [Required]
    public DateTime ModifiedOn { get; set; }

    [Required]
    public string ModifierId { get; set; } = null!;
}
