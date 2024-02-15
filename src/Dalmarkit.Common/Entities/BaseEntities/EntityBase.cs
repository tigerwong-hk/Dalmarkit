using Dalmarkit.Common.Entities.DataModels;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Entities.BaseEntities;

public abstract class EntityBase : IDataModelBase
{
    [Required]
    public string ClientId { get; set; } = null!;

    [Required]
    public DateTime CreatedOn { get; set; }

    [Required]
    public string CreateRequestId { get; set; } = null!;

    [Required]
    public string CreatorId { get; set; } = null!;
}
