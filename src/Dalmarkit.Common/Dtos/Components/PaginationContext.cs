using Dalmarkit.Common.Errors;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Dtos.Components;

public class PaginationContext
{
    [Range(Constants.MinPageNumber, Constants.MaxPageNumber, ErrorMessage = ErrorMessages.ModelStateErrors.RangeInclusiveExceeded)]
    public int PageNumber { get; set; }

    [Range(Constants.MinPageSize, Constants.MaxPageSize, ErrorMessage = ErrorMessages.ModelStateErrors.RangeInclusiveExceeded)]
    public int PageSize { get; set; }

    public PaginationContext()
    {
    }

    public PaginationContext(int pageNumber, int pageSize)
    {
        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    public int GetSkipCount()
    {
        return (PageNumber - 1) * PageSize;
    }
}
