using System.Diagnostics;

namespace Trackdub.Benchmarks.Metrics;

/// <summary>
/// Point-in-time snapshot of process memory, working set, and garbage collection metrics.
/// </summary>
/// <param name="WorkingSetBytes">Current physical memory allocated to the process (bytes).</param>
/// <param name="PeakWorkingSetBytes">Peak physical memory allocated to the process (bytes).</param>
/// <param name="ManagedAllocatedBytes">Total managed memory allocated by the GC since process start (bytes).</param>
/// <param name="Gen0Collections">Count of Generation 0 garbage collections since process start.</param>
/// <param name="Gen1Collections">Count of Generation 1 garbage collections since process start.</param>
/// <param name="Gen2Collections">Count of Generation 2 garbage collections since process start.</param>
public sealed record ResourceTelemetrySnapshot(
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections)
{
    /// <summary>
    /// Represents an empty telemetry snapshot with all metrics set to 0.
    /// </summary>
    public static ResourceTelemetrySnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Differential resource consumption between two point-in-time snapshots.
/// </summary>
/// <param name="WorkingSetDeltaBytes">Change in physical memory allocated (may be negative if memory was trimmed).</param>
/// <param name="PeakWorkingSetBytes">Highest peak physical memory observed across both snapshots (bytes).</param>
/// <param name="ManagedAllocatedBytes">Net managed memory allocated between start and end snapshots (bytes).</param>
/// <param name="Gen0Collections">Net Generation 0 garbage collections between start and end snapshots.</param>
/// <param name="Gen1Collections">Net Generation 1 garbage collections between start and end snapshots.</param>
/// <param name="Gen2Collections">Net Generation 2 garbage collections between start and end snapshots.</param>
public sealed record ResourceTelemetryDelta(
    long WorkingSetDeltaBytes,
    long PeakWorkingSetBytes,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections)
{
    /// <summary>
    /// Represents an empty telemetry delta with all metrics set to 0.
    /// </summary>
    public static ResourceTelemetryDelta Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Provides process-level resource telemetry sampling and delta calculation.
/// </summary>
public static class ResourceTelemetry
{
    /// <summary>
    /// Captures a fresh point-in-time snapshot of the current process resource usage.
    /// Refreshes process metrics from the OS and captures precise GC allocated bytes and collection counts.
    /// </summary>
    /// <returns>A populated <see cref="ResourceTelemetrySnapshot"/>.</returns>
    public static ResourceTelemetrySnapshot CaptureProcess()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        // On some platforms (e.g. macOS) the OS does not report a peak working set distinct
        // from the current one, so PeakWorkingSet64 can come back as 0 or below WorkingSet64.
        // Clamp it to WorkingSet64 so it always reflects at least the current usage.
        long workingSet = process.WorkingSet64;
        long peakWorkingSet = Math.Max(process.PeakWorkingSet64, workingSet);

        return new ResourceTelemetrySnapshot(
            WorkingSetBytes: workingSet,
            PeakWorkingSetBytes: peakWorkingSet,
            ManagedAllocatedBytes: GC.GetTotalAllocatedBytes(precise: true),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2));
    }

    /// <summary>
    /// Computes the differential resource consumption between two telemetry snapshots.
    /// </summary>
    /// <param name="start">The starting telemetry snapshot.</param>
    /// <param name="end">The ending telemetry snapshot.</param>
    /// <returns>A populated <see cref="ResourceTelemetryDelta"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="start"/> or <paramref name="end"/> is null.</exception>
    public static ResourceTelemetryDelta CalculateDelta(
        ResourceTelemetrySnapshot start,
        ResourceTelemetrySnapshot end)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);

        // Process.PeakWorkingSet64 only ever grows for the process's whole lifetime, so
        // Math.Max(start.Peak, end.Peak) reports the peak since process start, not the peak
        // within [start, end] — later intervals in a long-lived process (e.g. later providers
        // in the same matrix run) would inherit the highest peak any earlier interval reached.
        // Approximate the interval's own peak from its boundary snapshots instead; this misses
        // a spike that both rose and receded strictly inside the interval, but never attributes
        // an earlier interval's peak to a later one.
        return new ResourceTelemetryDelta(
            WorkingSetDeltaBytes: end.WorkingSetBytes - start.WorkingSetBytes,
            PeakWorkingSetBytes: Math.Max(start.WorkingSetBytes, end.WorkingSetBytes),
            ManagedAllocatedBytes: Math.Max(0, end.ManagedAllocatedBytes - start.ManagedAllocatedBytes),
            Gen0Collections: Math.Max(0, end.Gen0Collections - start.Gen0Collections),
            Gen1Collections: Math.Max(0, end.Gen1Collections - start.Gen1Collections),
            Gen2Collections: Math.Max(0, end.Gen2Collections - start.Gen2Collections));
    }
}
