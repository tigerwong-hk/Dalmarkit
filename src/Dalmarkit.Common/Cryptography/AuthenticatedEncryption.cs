using System.Security.Cryptography;
using System.Text;

namespace Dalmarkit.Common.Cryptography;

public static class AuthenticatedEncryption
{
    public const int DefaultKeyByteSize = 32;
    public const int DefaultNonceByteSize = 12;
    public const int DefaultTagByteSize = 16;

    public static string Decrypt(string encryptedText, string secretKey)
    {
        if (string.IsNullOrWhiteSpace(encryptedText))
        {
            throw new ArgumentNullException(nameof(encryptedText));
        }

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new ArgumentNullException(nameof(secretKey));
        }

        byte[] secretKeyBytes = Convert.FromBase64String(secretKey);
        if (secretKeyBytes.Length != DefaultKeyByteSize)
        {
            throw new ArgumentException($"Secret key length {secretKeyBytes.Length} must be {DefaultKeyByteSize}", nameof(secretKey));
        }

        byte[] encryptedBytes = Convert.FromBase64String(encryptedText);
        if (encryptedBytes.Length <= DefaultNonceByteSize + DefaultTagByteSize)
        {
            throw new ArgumentException($"Encrypted text length {encryptedBytes.Length} is less than sum of nonce and tag length {DefaultNonceByteSize + DefaultTagByteSize}", nameof(encryptedText));
        }

        byte[] nonce = new byte[DefaultNonceByteSize];
        byte[] tag = new byte[DefaultTagByteSize];
        byte[] ciphertext = new byte[encryptedBytes.Length - DefaultNonceByteSize - DefaultTagByteSize];
        byte[] plaintextBytes = new byte[ciphertext.Length];

        Buffer.BlockCopy(encryptedBytes, 0, nonce, 0, DefaultNonceByteSize);
        Buffer.BlockCopy(encryptedBytes, DefaultNonceByteSize, tag, 0, DefaultTagByteSize);
        Buffer.BlockCopy(encryptedBytes, DefaultNonceByteSize + DefaultTagByteSize, ciphertext, 0, ciphertext.Length);

        using AesGcm aes = new(secretKeyBytes, DefaultTagByteSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    public static string Encrypt(string plaintext, string secretKey)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            throw new ArgumentNullException(nameof(plaintext));
        }

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new ArgumentNullException(nameof(secretKey));
        }

        byte[] secretKeyBytes = Convert.FromBase64String(secretKey);
        if (secretKeyBytes.Length != DefaultKeyByteSize)
        {
            throw new ArgumentException($"Secret key length {secretKeyBytes.Length} must be {DefaultKeyByteSize}", nameof(secretKey));
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(DefaultNonceByteSize);
        byte[] tag = new byte[DefaultTagByteSize];
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] ciphertext = new byte[plaintextBytes.Length];

        using AesGcm aes = new(secretKeyBytes, DefaultTagByteSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        byte[] encryptedBytes = new byte[DefaultNonceByteSize + DefaultTagByteSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, encryptedBytes, 0, DefaultNonceByteSize);
        Buffer.BlockCopy(tag, 0, encryptedBytes, DefaultNonceByteSize, DefaultTagByteSize);
        Buffer.BlockCopy(ciphertext, 0, encryptedBytes, DefaultNonceByteSize + DefaultTagByteSize, ciphertext.Length);

        return Convert.ToBase64String(encryptedBytes, 0, encryptedBytes.Length);
    }
}
