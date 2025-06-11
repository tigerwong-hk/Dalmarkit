namespace Dalmarkit.Cloud.Aws.Constants;

public static class S3Bucket
{
    public const int MaxLength = 63;
    public const int MinLength = 3;
    public const string RegexPattern = @"^[0-9A-Za-z\.\-_]*(?<!\.)$";
    public const double RegexTimeoutIntervalMsec = 10000;
}
