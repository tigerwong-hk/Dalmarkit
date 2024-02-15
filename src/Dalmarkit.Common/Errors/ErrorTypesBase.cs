namespace Dalmarkit.Common.Errors;

public abstract class ErrorTypesBase
{
    protected static ErrorDetail GetErrorDetail(string code, string message)
    {
        return new(code, message);
    }
}
