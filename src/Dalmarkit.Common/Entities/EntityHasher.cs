using System.Security.Cryptography;
using System.Text.Json;

namespace Dalmarkit.Common.Entities;

public static class EntityHasher
{
    public static string Hash<TEntity>(TEntity entity)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(entity);
        byte[] hashed = SHA512.HashData(data);

        return Convert.ToBase64String(hashed);
    }
}
