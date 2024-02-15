using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Dalmarkit.Common.Validation;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Dalmarkit.Cloud.Aws.Services;

public class AwsCloudFrontService(AmazonCloudFrontClient cloudFrontClient) : IAwsCloudFrontService
{
    private static readonly CompositeFormat _cannedPolicyTemplate = CompositeFormat.Parse(@"{{""Statement"":[{{""Resource"":""{0}"",""Condition"":{{""DateLessThan"":{{""AWS:EpochTime"":{1}}}}}}}]}}");
    private readonly AmazonCloudFrontClient _cloudFrontClient = Guard.NotNull(cloudFrontClient, nameof(cloudFrontClient));

    public static string CreateCannedPolicyStatement(Uri resourceUrl, DateTimeOffset dateLessThan)
    {
        _ = Guard.NotNull(resourceUrl, nameof(resourceUrl));
        return DateTimeOffset.UtcNow >= dateLessThan
            ? throw new ArgumentException($"Expired: {dateLessThan}", nameof(dateLessThan))
            : string.Format(CultureInfo.InvariantCulture, _cannedPolicyTemplate, resourceUrl.AbsoluteUri, dateLessThan.ToUnixTimeSeconds());
    }

    public static string ToUrlSafeBase64String(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('=', '_')
            .Replace('/', '~');
    }

    public async Task AssociateAliasAsync(string cname, string cdnCacheId, CancellationToken cancellationToken)
    {
        UriHostNameType uriHostnameType = Uri.CheckHostName(cname);
        if (uriHostnameType != UriHostNameType.Dns)
        {
            throw new ArgumentException($"Invalid CNAME: {cname}", nameof(cname));
        }

        AssociateAliasRequest associateAliasRequest = new()
        {
            Alias = cname,
            TargetDistributionId = cdnCacheId,
        };

        AssociateAliasResponse associateAliasResponse = await _cloudFrontClient.AssociateAliasAsync(associateAliasRequest, cancellationToken);
        if (associateAliasResponse.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for AssociateAlias({cname}, {cdnCacheId} request: {associateAliasResponse.HttpStatusCode}");
        }
    }

    public Uri CreateReadOnlySignedUrl(Uri resourceUrl, int durationSecs, string privateKeyPem, string publicKeyId)
    {
        _ = Guard.NotNull(resourceUrl, nameof(resourceUrl));
        if (durationSecs <= 0)
        {
            throw new ArgumentException($"Duration in seconds must be positive: {durationSecs}", nameof(durationSecs));
        }

        TimeSpan duration = new(0, 0, durationSecs);
        DateTimeOffset dateLessThan = DateTimeOffset.UtcNow.Add(duration);
        string strExpires = dateLessThan.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        string strPolicy = CreateCannedPolicyStatement(resourceUrl, dateLessThan);
        byte[] bufferPolicy = Encoding.UTF8.GetBytes(strPolicy);

        using RSA rsaProvider = RSA.Create();
        rsaProvider.ImportFromPem(privateKeyPem);
        byte[] signedPolicyHash = rsaProvider.SignData(bufferPolicy, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1); //DevSkim: ignore DS126858
        string strSignedPolicy = ToUrlSafeBase64String(signedPolicyHash);

        return new($"{resourceUrl}?Expires={strExpires}&Signature={strSignedPolicy}&Key-Pair-Id={publicKeyId}");
    }
}
