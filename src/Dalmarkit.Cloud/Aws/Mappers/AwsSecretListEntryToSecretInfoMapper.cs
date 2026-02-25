using Amazon.SecretsManager.Model;
using Dalmarkit.Common.Services;
using Riok.Mapperly.Abstractions;

namespace Dalmarkit.Cloud.Aws.Mappers;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class AwsSecretListEntryToSecretInfoMapper
{
    [MapProperty(nameof(SecretListEntry.KmsKeyId), nameof(SecretInfo.EncryptionKeyId))]
    [MapProperty(nameof(SecretListEntry.Description), nameof(SecretInfo.SecretDescription))]
    [MapProperty(nameof(SecretListEntry.ARN), nameof(SecretInfo.SecretId))]
    [MapProperty(nameof(SecretListEntry.Name), nameof(SecretInfo.SecretName))]
    public static partial SecretInfo ToTarget(SecretListEntry source);

    public static partial IEnumerable<SecretInfo> ToTarget(IEnumerable<SecretListEntry> source);
}
