using Dalmarkit.Common.Entities.DataModels;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Entities.BaseEntities;

public abstract class DependentEntityBase<TMultipleEntity> : IDataModelPrincipalId, IDataModelSelfId
    where TMultipleEntity : IDataModelMultiple, IDataModelBase
{
    [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public virtual Guid PrincipalId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public virtual Guid SelfId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
}
