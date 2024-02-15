namespace Dalmarkit.Common.Services;

public interface IObjectStorageService
{
    Task InvalidateObjectsInCdnAsync(List<string> objectPaths, string cdnCacheId, string traceId, CancellationToken cancellationToken = default);
    Task UploadObjectAsync(Guid parentId, Guid objectId, string objectExtension, Stream stream, string bucketName, string folderName, string? cdnCacheId, bool isReplaceObject, string traceId, CancellationToken cancellationToken = default);
}
