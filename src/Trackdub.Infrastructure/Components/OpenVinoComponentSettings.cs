namespace Trackdub.Infrastructure.Components;

/// <summary>
/// Configuration settings for the OpenVINO component download and verification.
/// </summary>
public sealed record OpenVinoComponentSettings
{
    /// <summary>
    /// The URL to download the OpenVINO component package from.
    /// </summary>
    public required string DownloadUrl { get; init; }

    /// <summary>
    /// Expected SHA-256 hash of the downloaded package (lowercase hex).
    /// If null or empty, only file size verification is performed.
    /// </summary>
    public string? ExpectedSha256Hash { get; init; }

    /// <summary>
    /// Expected file size in bytes of the downloaded package.
    /// Used as a secondary integrity check when hash is not available.
    /// </summary>
    public long? ExpectedFileSizeBytes { get; init; }

}
