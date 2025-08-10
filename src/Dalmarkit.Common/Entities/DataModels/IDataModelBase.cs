namespace Dalmarkit.Common.Entities.DataModels;

public interface IDataModelBase
{
    string AppClientId { get; set; }
    DateTime CreatedOn { get; set; }
    string CreateRequestId { get; set; }
    string CreatorId { get; set; }
}
