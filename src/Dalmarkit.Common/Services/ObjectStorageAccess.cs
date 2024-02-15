namespace Dalmarkit.Common.Services;

public static class ObjectStorageAccess
{
    public static string GetPublicStorageObjectUrl(string baseUrl,
        string rootFolderName,
        Guid parentId,
        Guid objectId,
        string fileExtension)
    {
        Uri objectUrl = new($"https://{baseUrl}/{rootFolderName}/{parentId}/{objectId:N}.{fileExtension}");
        return objectUrl.AbsoluteUri;
    }

    public static string GetReadOnlyStorageObjectUrl(ICdnService cdnService,
        string baseUrl,
        string rootFolderName,
        Guid parentId,
        Guid objectId,
        string fileExtension,
        int durationSecs,
        string privateKeyPem,
        string publicKeyId)
    {
        Uri objectUrl = new($"https://{baseUrl}/{rootFolderName}/{parentId}/{objectId:N}.{fileExtension}");
        Uri signedUrl = cdnService.CreateReadOnlySignedUrl(objectUrl, durationSecs, privateKeyPem, publicKeyId);
        return signedUrl.AbsoluteUri;
    }
}
