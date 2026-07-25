using System.Security.Cryptography;

namespace Trackdub.TestDoubles;

/// <summary>
/// Deterministic SortFormer stand-in bytes for tests that exercise diarization download/import paths
/// without bundling the full ONNX artifact (~492 MB).
/// </summary>
public static class SortFormerTestFixtures
{
    public static readonly byte[] ModelBytes = [1, 2, 3, 4];

    public static string ExpectedSha256 { get; } =
        Convert.ToHexString(SHA256.HashData(ModelBytes)).ToLowerInvariant();
}
