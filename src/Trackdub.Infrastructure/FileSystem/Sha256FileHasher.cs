using System.Security.Cryptography;

namespace Trackdub.Infrastructure.FileSystem;

public sealed class Sha256FileHasher
{
    public string Compute(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);
        using FileStream stream = File.OpenRead(fullPath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
