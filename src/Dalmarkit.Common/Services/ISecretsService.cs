namespace Dalmarkit.Common.Services;

public interface ISecretsService
{
    Task<DateTimeOffset?> DeleteSecretAsync(string secretName, int? recoveryWindowDays = null,
        CancellationToken cancellationToken = default);

    Task<SecretInfo> GetSecretInfoAsync(string secretName, CancellationToken cancellationToken = default);

    Task<string> GetSecretStringAsync(string secretName, CancellationToken cancellationToken = default);

    Task<List<SecretInfo>> ListSecretInfosAsync(string secretNamePrefix,
        CancellationToken cancellationToken = default);

    Task<string> SetSecretStringAsync(string secretName, string secretValue,
        string? secretDescription = null, string? encryptionKeyId = null, CancellationToken cancellationToken = default);

    Task<string> UpdateSecretStringAsync(string secretName, string secretValue,
        CancellationToken cancellationToken = default);
}

public class SecretInfo
{
    public required string SecretId { get; set; }
    public required string SecretName { get; set; }
    public string? SecretDescription { get; set; }
    public string? EncryptionKeyId { get; set; }
    public DateTimeOffset? CreatedDate { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }
    public DateTimeOffset? LastAccessedDate { get; set; }
    public DateTimeOffset? LastChangedDate { get; set; }
    public DateTimeOffset? LastRotatedDate { get; set; }
}
