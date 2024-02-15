namespace Dalmarkit.Common.Entities.DataModels;

public interface IDataModelMultipleReadWrite : IDataModelReadWrite
{
    string EntityHash { get; set; }
}
