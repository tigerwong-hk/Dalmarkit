using Dalmarkit.Common.Entities.DataModels;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Entities.BaseEntities;

public abstract class DependentReadWriteEntityBase : MultipleReadWriteEntityBase, IDataModelPrincipalId, IDataModelSelfId
{
    [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public virtual Guid PrincipalId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public virtual Guid SelfId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
}
