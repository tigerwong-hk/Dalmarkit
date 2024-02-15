using Dalmarkit.Common.Errors;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public class NotDefaultAttribute : ValidationAttribute
{
    public NotDefaultAttribute() : base(ErrorMessages.ModelStateErrors.ValueNotDefault)
    {
    }

    public override bool IsValid(object? value)
    {
        if (value is null)
        {
            return true;
        }

        Type type = value.GetType();
        if (!type.IsValueType)
        {
            return true;
        }

        object? defaultValue = Activator.CreateInstance(type);
        return !value.Equals(defaultValue);
    }
}
