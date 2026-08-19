using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Validation;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Dtos.InputDtos;

public abstract class UpdateDependentsInputDto<TInputDto> : ParentInputDto where TInputDto : IDependentInputDto
{
    [Required(ErrorMessage = ErrorMessages.ModelStateErrors.FieldRequired)]
    [NotDefault(ErrorMessage = ErrorMessages.ModelStateErrors.ValueNotDefault)]
    [MinLength(Constants.MinNumberOfDependents, ErrorMessage = ErrorMessages.ModelStateErrors.ElementsTooFew)]
    [MaxLength(Constants.MaxNumberOfDependents, ErrorMessage = ErrorMessages.ModelStateErrors.ElementsTooMany)]
    public IEnumerable<TInputDto> Dependents { get; set; } = default!;
}
