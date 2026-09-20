using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.TestDoubles;

/// <summary>
/// A configurable hash verifier for tests. Returns predetermined results or
/// computes real SHA-256 hashes based on configuration.
/// </summary>
public sealed class ConfigurableModelHashVerifier : IModelHashVerifier
{
    private readonly Dictionary<string, string> fileToActualHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> filesToFail = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a file path to return a specific actual hash (simulating corruption).
    /// </summary>
    public void RegisterActualHash(string filePath, string actualSha256)
    {
        fileToActualHash[filePath] = actualSha256;
    }

    /// <summary>
    /// Marks a file path to fail verification (hash mismatch).
    /// </summary>
    public void MarkAsCorrupt(string filePath)
    {
        filesToFail.Add(filePath);
    }

    public Task<HashVerificationResult> VerifyAsync(
        ModelManifest manifest,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        string? expectedHash = manifest.Sha256?.Trim();
        return VerifyCore(expectedHash, filePath);
    }

    public Task<HashVerificationResult> VerifyAsync(
        string? expectedSha256,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        return VerifyCore(expectedSha256, filePath);
    }

    private Task<HashVerificationResult> VerifyCore(string? expectedHash, string filePath)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return Task.FromResult(new HashVerificationResult(true, false, null, null, "No hash to verify."));
        }

        string? actualHash;
        if (filesToFail.Contains(filePath))
        {
            actualHash = "corrupted_hash_value";
        }
        else if (fileToActualHash.TryGetValue(filePath, out string? registered))
        {
            actualHash = registered;
        }
        else
        {
            // Compute real hash from file content
            if (!File.Exists(filePath))
            {
                return Task.FromResult(new HashVerificationResult(false, false, expectedHash, null, "File does not exist."));
            }
            byte[] content = File.ReadAllBytes(filePath);
            actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        }

        bool isValid = string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new HashVerificationResult(
            isValid,
            true,
            expectedHash,
            actualHash,
            isValid ? null : "Hash mismatch."));
    }
}
