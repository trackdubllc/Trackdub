namespace Trackdub.Contracts.Licensing;

/// <summary>
/// Progress information for a model download.
/// </summary>
public sealed record ModelDownloadProgress(
    long BytesDownloaded,
    long? TotalBytes,
    int PercentComplete,
    string? Message,
    double? DownloadSpeedBytesPerSecond = null,
    TimeSpan? EstimatedTimeRemaining = null);

/// <summary>
/// Application-layer contract for downloading models.
/// </summary>
public interface IModelDownloaderContract
{
    /// <summary>
    /// Downloads a model file from Hugging Face Hub.
    /// </summary>
    Task<bool> DownloadAsync(
        string modelId,
        string fileName,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? revision = null);

    /// <summary>
    /// Downloads a runtime support file from an explicit source URI.
    /// </summary>
    Task<bool> DownloadUriAsync(
        Uri sourceUri,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);



    /// <summary>
    /// Verifies the SHA-256 hash of a file against an expected value.
    /// </summary>
    Task<bool> VerifyHashAsync(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken = default);
}
