namespace Dalmarkit.Common.Entities.DataModels;

public interface IDataModelReadWrite : IDataModelBase
{
    bool IsDeleted { get; set; }
    DateTime ModifiedOn { get; set; }
    string ModifierId { get; set; }
}
