namespace Dalmarkit.Common.Api.Responses;

public class ResponsePagination<T>(IEnumerable<T> data, int filteredCount, int pageNumber, int pageSize)
{
    public IEnumerable<T> Data { get; set; } = data;
    public int FilteredCount { get; set; } = filteredCount;
    public int PageNumber { get; set; } = pageNumber;
    public int PageSize { get; set; } = pageSize;
}
