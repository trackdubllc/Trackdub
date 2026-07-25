namespace Trackdub.Application.Runtime;

/// <summary>
/// Locates, downloads when allowed, and registers the TensorRT RTX standalone ORT EP ABI plugin.
/// </summary>
public interface ITrtRtxEpInstaller
{
    /// <summary>
    /// Ensures the TRT RTX execution provider plugin is registered with the ORT
    /// environment. Reports human-readable status strings via <paramref name="progress"/> as the
    /// operation proceeds. Returns a result indicating whether the EP is now usable.
    /// </summary>
    Task<TrtRtxEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a TRT RTX EP plugin registration attempt.
/// </summary>
/// <param name="Succeeded">True when the plugin EP is registered and should be usable in the next ORT session.</param>
/// <param name="FailureDetail">Human-readable reason for failure, or null on success.</param>
public sealed record TrtRtxEpInstallResult(
    bool Succeeded,
    string? FailureDetail = null);
