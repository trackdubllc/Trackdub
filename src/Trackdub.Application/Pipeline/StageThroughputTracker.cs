using System.Diagnostics;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Computes best-effort per-stage ETA from item throughput.
/// Instantiate once per stage run; call Report after each item completes.
/// </summary>
public sealed class StageThroughputTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <summary>
    /// Call after each item completes.
    /// Returns best-effort ETA or null when insufficient history (&lt;200 ms elapsed).
    /// Returns null when itemsComplete &gt;= totalItems (already done).
    /// </summary>
    public TimeSpan? Report(int itemsComplete, int totalItems)
    {
        if (itemsComplete <= 0 || totalItems <= itemsComplete)
            return null;

        double elapsedMs = _stopwatch.Elapsed.TotalMilliseconds;
        if (elapsedMs < 200)
            return null; // Too early to project accurately.

        double msPerItem = elapsedMs / itemsComplete;
        int remaining = totalItems - itemsComplete;
        return TimeSpan.FromMilliseconds(msPerItem * remaining);
    }

    /// <summary>Elapsed time since the tracker was created.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;
}
