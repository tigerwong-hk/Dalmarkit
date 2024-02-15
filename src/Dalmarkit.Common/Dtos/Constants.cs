namespace Dalmarkit.Common.Dtos;

public static class Constants
{
    public const int MaxLengthCreateRequestId = 64;
    public const int MaxLengthObjectExtension = 32;
    public const int MaxLengthObjectName = 128;
    public const int MaxNumberOfDependents = 100;
    public const int MaxPageNumber = (int.MaxValue / MaxPageSize) + 1;
    public const int MaxPageSize = 100;
    public const int MinNumberOfDependents = 1;
    public const int MinPageNumber = 1;
    public const int MinPageSize = 10;
}
