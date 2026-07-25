namespace Trackdub.Infrastructure.Licensing;

/// <summary>
/// Progress information for a model download.
/// </summary>
public sealed record DownloadProgress(
    long BytesDownloaded,
    long? TotalBytes,
    int PercentComplete,
    string? Message,
    double? DownloadSpeedBytesPerSecond = null,
    TimeSpan? EstimatedTimeRemaining = null);

/// <summary>
/// Downloads ONNX models from Hugging Face Hub.
/// </summary>
public interface IModelDownloader
{
    /// <summary>
    /// Downloads a model file from Hugging Face Hub.
    /// </summary>
    /// <param name="modelId">Model ID (e.g., "cgus/diar_streaming_sortformer_4spk-v2.1-onnx")</param>
    /// <param name="fileName">File name within the model repo (e.g., "diar_streaming_sortformer_4spk-v2.1.onnx")</param>
    /// <param name="destinationPath">Full path where the file should be saved</param>
    /// <param name="progress">Optional progress reporter for download status updates.</param>
    /// <param name="revision">Optional Hugging Face revision, commit, branch, or tag. Defaults to main.</param>
    /// <param name="cancellationToken">Cancellation token for the download</param>
    /// <returns>True if download and verification succeeded; false otherwise</returns>
    Task<bool> DownloadAsync(
        string modelId,
        string fileName,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? revision = null);

    /// <summary>
    /// Verifies the SHA-256 hash of a downloaded file.
    /// </summary>
    Task<bool> VerifyHashAsync(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken = default);
}
