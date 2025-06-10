using Dalmarkit.Common.Errors;

namespace Dalmarkit.Common.Validation;

public static class Guard
{
    public static Guid NotEmpty(Guid value, string parameterName)
    {
#pragma warning disable RCS1238 // Avoid nested ?: operators
        return string.IsNullOrWhiteSpace(parameterName)
            ? throw new ArgumentException(ErrorMessages.ParameterNameMissing, nameof(parameterName))
            : (value == Guid.Empty ? throw new ArgumentException(ErrorMessages.ValueIsEmpty, parameterName) : value);
#pragma warning restore RCS1238 // Avoid nested ?: operators
    }

    public static T NotNull<T>(T value, string parameterName)
    {
#pragma warning disable RCS1238 // Avoid nested ?: operators
        return string.IsNullOrWhiteSpace(parameterName)
            ? throw new ArgumentException(ErrorMessages.ParameterNameMissing, nameof(parameterName))
            : (value is null ? throw new ArgumentNullException(parameterName) : value);
#pragma warning restore RCS1238 // Avoid nested ?: operators
    }

    public static string NotNullOrWhiteSpace(string? value, string parameterName)
    {
#pragma warning disable RCS1238 // Avoid nested ?: operators
        return string.IsNullOrWhiteSpace(parameterName)
            ? throw new ArgumentException(ErrorMessages.ParameterNameMissing, nameof(parameterName))
            : (string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(ErrorMessages.ValueIsNullOrWhiteSpace, parameterName) : value);
#pragma warning restore RCS1238 // Avoid nested ?: operators
    }
}
