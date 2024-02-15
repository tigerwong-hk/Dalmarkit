namespace Dalmarkit.Common.Services;

public interface ICdnService
{
    Task AssociateAliasAsync(string cname, string cdnCacheId, CancellationToken cancellationToken);
    Uri CreateReadOnlySignedUrl(Uri resourceUrl, int durationSecs, string privateKeyPem, string publicKeyId);
}
