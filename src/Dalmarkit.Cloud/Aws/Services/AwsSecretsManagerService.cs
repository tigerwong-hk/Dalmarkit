using Amazon.SecretsManager;
using Amazon.SecretsManager.Extensions.Caching;
using Amazon.SecretsManager.Model;
using Dalmarkit.Cloud.Aws.Mappers;
using Dalmarkit.Common.Services;
using Dalmarkit.Common.Validation;
using System.Net;
using System.Text.RegularExpressions;

namespace Dalmarkit.Cloud.Aws.Services;

public class AwsSecretsManagerService(AmazonSecretsManagerClient secretsManagerClient, SecretsManagerCache secretsManagerCache) : IAwsSecretsManagerService
{
    public const int EncryptionKeyIdMaxLength = 2048;
    public const int SecretDescriptionMaxLength = 2048;
    public const int SecretInfosMaxResults = 100;
    public const int SecretNameMaxLength = 256;
    public const string SecretNameRegexPattern = "^[a-zA-Z0-9/_+=.@-]{1,512}$";
    public const string SecretNameReservedSuffixRegexPattern = "-[a-zA-Z0-9/_+=.@-]{6}$";
    public const int SecretValueMaxLength = 65536;
    public const double RegexTimeoutIntervalMsec = 10000;

    private readonly AmazonSecretsManagerClient _secretsManagerClient =
        Guard.NotNull(secretsManagerClient, nameof(secretsManagerClient));

    private readonly SecretsManagerCache _secretsManagerCache =
        Guard.NotNull(secretsManagerCache, nameof(secretsManagerCache));

    public async Task<DateTimeOffset?> DeleteSecretAsync(string secretName, int? recoveryWindowDays = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSecretName(secretName);

        if (recoveryWindowDays is not null and ((not 0 and < 7) or > 30))
        {
            throw new ArgumentException($"Invalid recovery window {recoveryWindowDays}", nameof(recoveryWindowDays));
        }

        DeleteSecretResponse response = await _secretsManagerClient.DeleteSecretAsync(new DeleteSecretRequest
        {
            ForceDeleteWithoutRecovery = recoveryWindowDays is not null and 0 ? true : null,
            RecoveryWindowInDays = recoveryWindowDays is not null and not 0 ? recoveryWindowDays : null,
            SecretId = secretName
        }, cancellationToken);
#pragma warning disable IDE0046 // Convert to conditional expression
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for DeleteSecret({secretName}) request: {response.HttpStatusCode}",
                null, response.HttpStatusCode);
        }
#pragma warning restore IDE0046 // Convert to conditional expression

        return response.DeletionDate != null
            ? new(response.DeletionDate.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(response.DeletionDate.Value, DateTimeKind.Utc)
                : response.DeletionDate.Value)
            : null;
    }

    public async Task<SecretInfo> GetSecretInfoAsync(string secretName, CancellationToken cancellationToken = default)
    {
        ValidateSecretName(secretName);

        DescribeSecretResponse response = await _secretsManagerClient.DescribeSecretAsync(new DescribeSecretRequest
        {
            SecretId = secretName
        }, cancellationToken);
        return response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous
            ? throw new HttpRequestException($"Error status for DescribeSecret({secretName}) request: {response.HttpStatusCode}", null, response.HttpStatusCode)
            : AwsDescribeSecretResponseToSecretInfoMapper.ToTarget(response);
    }

    public async Task<string> GetSecretStringAsync(string secretName, CancellationToken cancellationToken = default)
    {
        ValidateSecretName(secretName);

        return await _secretsManagerCache.GetSecretString(secretName, cancellationToken);
    }

    public async Task<List<SecretInfo>> ListSecretInfosAsync(string secretNamePrefix, CancellationToken cancellationToken = default)
    {
        ValidateSecretName(secretNamePrefix);

        List<SecretInfo> secretInfos = [];
        string? nextToken = null;

        do
        {
            ListSecretsResponse response = await _secretsManagerClient.ListSecretsAsync(new ListSecretsRequest
            {
                Filters = [new Filter
                {
                    Key = FilterNameStringType.Name,
                    Values = [secretNamePrefix]
                }],
                MaxResults = SecretInfosMaxResults,
                SortOrder = SortOrderType.Asc,
                NextToken = nextToken
            }, cancellationToken);
            if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
            {
                throw new HttpRequestException($"Error status for ListSecrets({secretNamePrefix}) request: {response.HttpStatusCode}", null, response.HttpStatusCode);
            }

            nextToken = response.NextToken;
            if (response.SecretList != null)
            {
                secretInfos.AddRange(AwsSecretListEntryToSecretInfoMapper.ToTarget(response.SecretList));
            }
        } while (!string.IsNullOrWhiteSpace(nextToken));

        return secretInfos;
    }

    public async Task<string> SetSecretStringAsync(string secretName, string secretValue, string? secretDescription = null, string? encryptionKeyId = null, CancellationToken cancellationToken = default)
    {
        ValidateSecretName(secretName);

        if (string.IsNullOrWhiteSpace(secretValue))
        {
            throw new ArgumentException("No value", nameof(secretValue));
        }

        if (secretValue.Length > SecretValueMaxLength)
        {
            throw new ArgumentException($"Value length {secretValue.Length} exceed {SecretValueMaxLength}", nameof(secretValue));
        }

        if (!string.IsNullOrWhiteSpace(secretDescription) && secretDescription.Length > SecretDescriptionMaxLength)
        {
            throw new ArgumentException($"Description length {secretDescription.Length} exceed {SecretDescriptionMaxLength}", nameof(secretDescription));
        }

        if (!string.IsNullOrWhiteSpace(encryptionKeyId) && encryptionKeyId.Length > EncryptionKeyIdMaxLength)
        {
            throw new ArgumentException($"Encryption key ID length {encryptionKeyId.Length} exceed {EncryptionKeyIdMaxLength}", nameof(encryptionKeyId));
        }

        CreateSecretResponse response = await _secretsManagerClient.CreateSecretAsync(new CreateSecretRequest
        {
            ClientRequestToken = Guid.NewGuid().ToString(),
            Description = secretDescription,
            KmsKeyId = encryptionKeyId,
            Name = secretName,
            SecretString = secretValue
        }, cancellationToken);
        return response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous
            ? throw new HttpRequestException($"Error status for CreateSecret({secretName}) request: {response.HttpStatusCode}", null, response.HttpStatusCode)
            : response.ARN;
    }

    public async Task<string> UpdateSecretStringAsync(string secretName, string secretValue, CancellationToken cancellationToken = default)
    {
        ValidateSecretName(secretName);

        if (string.IsNullOrWhiteSpace(secretValue))
        {
            throw new ArgumentException("No value", nameof(secretValue));
        }

        if (secretValue.Length > SecretValueMaxLength)
        {
            throw new ArgumentException($"Value length {secretValue.Length} exceed {SecretValueMaxLength}", nameof(secretValue));
        }

        PutSecretValueResponse response = await _secretsManagerClient.PutSecretValueAsync(new PutSecretValueRequest
        {
            ClientRequestToken = Guid.NewGuid().ToString(),
            SecretId = secretName,
            SecretString = secretValue
        }, cancellationToken);
        return response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous
            ? throw new HttpRequestException($"Error status for PutSecretValue({secretName}) request: {response.HttpStatusCode}", null, response.HttpStatusCode)
            : response.ARN;
    }

    private static void ValidateSecretName(string secretName)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new ArgumentException("No name", nameof(secretName));
        }

        if (secretName.Length > SecretNameMaxLength)
        {
            throw new ArgumentException($"Name length {secretName.Length} exceed {SecretNameMaxLength}", nameof(secretName));
        }

        if (Regex.IsMatch(secretName, SecretNameReservedSuffixRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutIntervalMsec)))
        {
            throw new ArgumentException($"Reserved name suffix in name {secretName}", nameof(secretName));
        }

#pragma warning disable IDE0046 // Convert to conditional expression
        if (Regex.IsMatch(secretName, SecretNameRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutIntervalMsec)))
        {
            return;
        }
#pragma warning restore IDE0046 // Convert to conditional expression

        throw new ArgumentException($"Invalid name {secretName}", nameof(secretName));
    }
}
