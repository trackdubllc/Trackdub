namespace Trackdub.Contracts.Licensing;

/// <summary>
/// Exception thrown when a required model is not available locally and cannot be auto-downloaded.
/// </summary>
public sealed class RequiredModelNotAvailableException(
    string modelId,
    string modelPath,
    bool canAutoDownload = false)
    : InvalidOperationException(BuildMessage(modelId, modelPath, canAutoDownload))
{
    public string ModelId { get; } = modelId;
    public string ModelPath { get; } = modelPath;
    public bool CanAutoDownload { get; } = canAutoDownload;

    private static string BuildMessage(string modelId, string modelPath, bool canAutoDownload)
    {
        if (canAutoDownload)
        {
            return $"Model '{modelId}' not found at '{modelPath}'. " +
                   "Use Download now, or retry after resolving network access. " +
                   "This is a one-time download of the ONNX diarization model.";
        }

        return $"Model '{modelId}' not found at '{modelPath}'. " +
               "Download or export the ONNX model, then import it or place it at " +
               $"'{modelPath}' before retrying.";
    }
}
