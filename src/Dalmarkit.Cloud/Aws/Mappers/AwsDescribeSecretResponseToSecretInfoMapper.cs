using Amazon.SecretsManager.Model;
using Dalmarkit.Common.Services;
using Riok.Mapperly.Abstractions;

namespace Dalmarkit.Cloud.Aws.Mappers;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class AwsDescribeSecretResponseToSecretInfoMapper
{
    [MapProperty(nameof(DescribeSecretResponse.KmsKeyId), nameof(SecretInfo.EncryptionKeyId))]
    [MapProperty(nameof(DescribeSecretResponse.Description), nameof(SecretInfo.SecretDescription))]
    [MapProperty(nameof(DescribeSecretResponse.ARN), nameof(SecretInfo.SecretId))]
    [MapProperty(nameof(DescribeSecretResponse.Name), nameof(SecretInfo.SecretName))]
    public static partial SecretInfo ToTarget(DescribeSecretResponse source);

    public static partial IEnumerable<SecretInfo> ToTarget(IEnumerable<DescribeSecretResponse> source);
}
