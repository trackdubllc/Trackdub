namespace Trackdub.Application.Runtime;

/// <summary>
/// Ensures and registers all certified Windows ML catalog execution providers on the machine.
/// This may trigger multiple package downloads on first run; callers must surface progress and
/// allow cancellation. Distinct from per-EP installers (MIGraphX, TensorRT RTX) which target a
/// single provider.
/// </summary>
public interface IWindowsMlCertifiedCatalogInstaller
{
    /// <summary>
    /// Runs Windows ML catalog ensure-and-register for all certified providers. Reports
    /// human-readable status via <paramref name="progress"/>. Does not imply every EP is ready
    /// for inference on this hardware.
    /// </summary>
    Task<WindowsMlCertifiedCatalogInstallResult> EnsureAllCertifiedAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a bulk certified-catalog ensure-and-register attempt.
/// </summary>
/// <param name="Succeeded">True when catalog ensure-and-register completed without failure.</param>
/// <param name="Detail">Human-readable summary (success or failure).</param>
/// <param name="FailureDetail">Optional failure reason when <paramref name="Succeeded"/> is false.</param>
public sealed record WindowsMlCertifiedCatalogInstallResult(
    bool Succeeded,
    string Detail,
    string? FailureDetail = null);
