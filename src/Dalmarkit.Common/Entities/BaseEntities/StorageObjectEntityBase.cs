using Dalmarkit.Common.Entities.DataModels;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Entities.BaseEntities;

public abstract class StorageObjectEntityBase : ReadWriteEntityBase, IDataModelPrincipalId, IDataModelSelfId, IDataModelStorageObject
{
    [Required]
    public string ObjectExtension { get; set; } = null!;

    [Required]
    public string ObjectName { get; set; } = null!;

    [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public virtual Guid PrincipalId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public virtual Guid SelfId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
}
