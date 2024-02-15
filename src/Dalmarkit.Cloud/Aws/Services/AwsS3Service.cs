using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.S3.Transfer;
using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Validation;
using Microsoft.Extensions.Logging;

namespace Dalmarkit.Cloud.Aws.Services;

public class AwsS3Service(TransferUtility transferUtility,
    AmazonCloudFrontClient cloudFrontClient,
    ILogger<AwsS3Service> logger) : IAwsS3Service
{
    private readonly AmazonCloudFrontClient _cloudFrontClient = Guard.NotNull(cloudFrontClient, nameof(cloudFrontClient));
    private readonly TransferUtility _transferUtility = Guard.NotNull(transferUtility, nameof(transferUtility));
    private readonly ILogger _logger = Guard.NotNull(logger, nameof(logger));

    public async Task InvalidateObjectsInCdnAsync(List<string> objectPaths, string cdnCacheId, string traceId, CancellationToken cancellationToken = default)
    {
        try
        {
            List<string> escapedFilePaths = objectPaths.ConvertAll(x => '/' + EscapePathSegments(x));

            CreateInvalidationRequest invalidationRequest = new()
            {
                DistributionId = cdnCacheId,
                InvalidationBatch = new InvalidationBatch
                {
                    CallerReference = traceId,
                    Paths = new Paths
                    {
                        Items = escapedFilePaths,
                        Quantity = escapedFilePaths.Count
                    }
                }
            };

            _ = await _cloudFrontClient.CreateInvalidationAsync(invalidationRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.InvalidateFilesCloudFrontException(ex.Message, ex.InnerException?.Message, ex.StackTrace);
        }
    }

    public async Task UploadObjectAsync(Guid parentId,
        Guid objectId,
        string objectExtension,
        Stream stream,
        string bucketName,
        string folderName,
        string? cdnCacheId,
        bool isReplaceObject,
        string traceId,
        CancellationToken cancellationToken)
    {
        _ = Guard.NotEmpty(parentId, nameof(parentId));
        _ = Guard.NotEmpty(objectId, nameof(objectId));
        _ = Guard.NotNullOrWhiteSpace(objectExtension, nameof(objectExtension));
        _ = Guard.NotNull(stream, nameof(stream));
        _ = Guard.NotNullOrWhiteSpace(bucketName, nameof(bucketName));
        _ = Guard.NotNullOrWhiteSpace(folderName, nameof(folderName));
        _ = Guard.NotNull(traceId, nameof(traceId));

        if (isReplaceObject)
        {
            _ = Guard.NotNullOrWhiteSpace(cdnCacheId, nameof(cdnCacheId));
        }

        if (stream == Stream.Null)
        {
            throw new ArgumentException(ErrorMessages.StreamNull, nameof(stream));
        }

        string folder = $"{folderName}/{parentId}";
        string fileName = $"{objectId:N}.{objectExtension}";
        string destFilePath = Path.Combine(folder, fileName);

        await _transferUtility.UploadAsync(stream, bucketName, destFilePath, cancellationToken);

        if (isReplaceObject)
        {
            await InvalidateObjectsInCdnAsync([destFilePath], cdnCacheId!, traceId, cancellationToken);
        }
    }

    private static string EscapePathSegments(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> pathSegments = segments.Select(Uri.EscapeDataString).ToList();

        return string.Join('/', pathSegments);
    }
}

public static partial class AwsS3ServiceLogs
{
    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Error,
        Message = "Exception in InvalidateFilesInCloudFront with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void InvalidateFilesCloudFrontException(
        this ILogger logger, string message, string? innerException, string? stackTrace);
}
