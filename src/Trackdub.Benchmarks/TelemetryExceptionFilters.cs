namespace Trackdub.Benchmarks;

/// <summary>
/// Single home for the best-effort telemetry exception filters. Sampling must never
/// change stage execution, so each sampling boundary swallows only OS/plugin failures
/// and records them as unavailable instead. Each predicate preserves the exact
/// exception set its call site used inline before; the sets differ on purpose
/// (a periodic sampler can observe disposal races a point-in-time collector cannot),
/// so they are named per call site rather than merged.
/// </summary>
internal static class TelemetryExceptionFilters
{
    /// <summary>
    /// Failures the continuous working-set sampler treats as unavailable
    /// (previously inline in <see cref="WorkingSetPeakMonitor"/>).
    /// </summary>
    public static bool IsWorkingSetSamplingFailure(Exception exception) => exception is
        ObjectDisposedException or
        InvalidOperationException or
        NotSupportedException or
        System.ComponentModel.Win32Exception or
        UnauthorizedAccessException or
        System.Security.SecurityException;

    /// <summary>
    /// Failures the stage-boundary resource collector treats as unavailable
    /// (previously inline in <see cref="StageResourceTelemetryCapture"/>).
    /// </summary>
    public static bool IsCollectorSamplingFailure(Exception exception) => exception is
        System.ComponentModel.Win32Exception or
        NotSupportedException or
        UnauthorizedAccessException or
        InvalidOperationException or
        IOException;

    /// <summary>
    /// Failures the process snapshot probe treats as a missing snapshot
    /// (previously inline in <see cref="Metrics.ResourceTelemetry"/>).
    /// </summary>
    public static bool IsProcessCaptureFailure(Exception exception) => exception is
        System.ComponentModel.Win32Exception or
        NotSupportedException or
        InvalidOperationException;
}
