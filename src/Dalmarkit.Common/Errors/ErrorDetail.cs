using System.Globalization;

namespace Dalmarkit.Common.Errors;

public class ErrorDetail(string code, string message)
{
    public string Code { get; } = code;

    public string Message { get; private set; } = message;

    private string? MessageTemplate { get; set; }

    public ErrorDetail WithArgs(params object[] args)
    {
        if (args.Length > 0)
        {
            MessageTemplate = Message;
            Message = GetMessage(args);
        }

        return this;
    }

    private string GetMessage(params object[] args)
    {
        return string.Format(CultureInfo.InvariantCulture, MessageTemplate!, args);
    }
}
