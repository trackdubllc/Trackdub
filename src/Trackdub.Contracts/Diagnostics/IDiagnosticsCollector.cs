namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// Aggregates structured diagnostics information about the current application installation.
/// </summary>
public interface IDiagnosticsCollector
{
    /// <summary>
    /// Collects a point-in-time diagnostics snapshot containing log file paths, DB schema version,
    /// model cache state, hardware profile, OS version, and runtime version info.
    /// </summary>
    Task<DiagnosticsSnapshot> CollectAsync(CancellationToken cancellationToken = default);
}
