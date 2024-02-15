using Dalmarkit.Common.Errors;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Dtos.InputDtos;

public class UploadObjectInputDto : ParentInputDto
{
    [Required(ErrorMessage = ErrorMessages.ModelStateErrors.FieldRequired)]
    [StringLength(Constants.MaxLengthCreateRequestId, ErrorMessage = ErrorMessages.ModelStateErrors.LengthExceeded)]
    public string CreateRequestId { get; set; } = null!;

    [Required(ErrorMessage = ErrorMessages.ModelStateErrors.FieldRequired)]
    [StringLength(Constants.MaxLengthObjectExtension, ErrorMessage = ErrorMessages.ModelStateErrors.LengthExceeded)]
    public string ObjectExtension { get; set; } = null!;

    [Required(ErrorMessage = ErrorMessages.ModelStateErrors.FieldRequired)]
    [StringLength(Constants.MaxLengthObjectName, ErrorMessage = ErrorMessages.ModelStateErrors.LengthExceeded)]
    public string ObjectName { get; set; } = null!;
}
