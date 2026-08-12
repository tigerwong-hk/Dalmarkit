using Dalmarkit.Common.Errors;
using System.Collections;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Validation;

/// <summary>
/// Validates that a bound collection contains no <see langword="null"/> elements.
/// </summary>
/// <remarks>
/// Collection-level attributes (<see cref="RequiredAttribute"/>, <see cref="MinLengthAttribute"/>,
/// <see cref="MaxLengthAttribute"/>) constrain the collection, not its contents, and a non-nullable
/// element type does not stop a JSON <c>null</c> from being materialized into the collection. Without
/// this attribute the first code to dereference an element — an <see cref="IValidatableObject"/>
/// aggregate, a mapper — throws, and because model validation runs before the action that throw becomes
/// a 500 rather than a validation failure.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public class NoNullElementsAttribute : ValidationAttribute
{
    public NoNullElementsAttribute() : base(ErrorMessages.ModelStateErrors.ElementNull)
    {
    }

    public override bool IsValid(object? value)
    {
        // An absent collection is RequiredAttribute's business, not this one's — the same division of
        // labour NotDefaultAttribute observes by passing null through.
        if (value is null)
        {
            return true;
        }

        // A string is IEnumerable<char>, whose elements are value types and can never be null, so it is
        // trivially valid. Short-circuited rather than enumerated: otherwise a long string is walked
        // character by character to prove something that is true by construction.
        if (value is not IEnumerable enumerable || value is string)
        {
            return true;
        }

        foreach (object? element in enumerable)
        {
            if (element is null)
            {
                return false;
            }
        }

        return true;
    }
}
