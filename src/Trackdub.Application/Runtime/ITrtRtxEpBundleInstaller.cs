namespace Trackdub.Application.Runtime;

/// <summary>
/// Downloads and extracts the pinned TensorRT RTX EP ABI provider bundle into the standard
/// Trackdub provider directory. Does not register the plugin with ONNX Runtime.
/// </summary>
public interface ITrtRtxEpBundleInstaller
{
    Task<TrtRtxEpBundleInstallResult> EnsureBundleAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default);
}

public sealed record TrtRtxEpBundleInstallResult(
    bool Succeeded,
    string? InstallDirectory,
    string? FailureDetail = null);
