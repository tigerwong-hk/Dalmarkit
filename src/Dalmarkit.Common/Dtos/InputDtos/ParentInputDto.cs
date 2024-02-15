using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Validation;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Dtos.InputDtos;

public abstract class ParentInputDto
{
    [Required(ErrorMessage = ErrorMessages.ModelStateErrors.FieldRequired)]
    [NotDefault(ErrorMessage = ErrorMessages.ModelStateErrors.ValueNotDefault)]
    public Guid ParentId { get; set; }
}
