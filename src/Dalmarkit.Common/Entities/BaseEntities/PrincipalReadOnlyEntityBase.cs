using Dalmarkit.Common.Entities.DataModels;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Entities.BaseEntities;

public abstract class PrincipalReadOnlyEntityBase : ReadOnlyEntityBase, IDataModelSelfId
{
    [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public virtual Guid SelfId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
}
