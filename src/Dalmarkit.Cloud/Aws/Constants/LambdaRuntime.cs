namespace Dalmarkit.Cloud.Aws.Constants;

public static class LambdaRuntime
{
    public const string RegexPattern = @"^(dotnet8|go1\.x|java17|java21|nodejs2[02]\.x|python3\.1[23])$";
    public const double RegexTimeoutIntervalMsec = 10000;
}
