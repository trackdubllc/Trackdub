using System.Collections.Concurrent;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Tracks devices excluded during a pipeline run due to OOM or failure.
/// Thread-safe for concurrent stage planning.
/// </summary>
public sealed class DeviceExclusionSet
{
    private readonly ConcurrentDictionary<int, ExclusionReason> _exclusions = new();

    /// <summary>
    /// Marks a device as memory-exhausted for the current pipeline run.
    /// Idempotent — marking an already-excluded device is a no-op.
    /// </summary>
    public void MarkMemoryExhausted(int deviceIndex)
    {
        _exclusions.TryAdd(deviceIndex, ExclusionReason.MemoryExhausted);
    }

    /// <summary>
    /// Marks a device as failed for the current pipeline run.
    /// Idempotent — marking an already-excluded device is a no-op.
    /// </summary>
    public void MarkFailed(int deviceIndex, string reason)
    {
        _exclusions.TryAdd(deviceIndex, ExclusionReason.Failed);
    }

    /// <summary>
    /// Returns true if the device has been marked as excluded by either
    /// <see cref="MarkMemoryExhausted"/> or <see cref="MarkFailed"/>.
    /// </summary>
    public bool IsExcluded(int deviceIndex)
    {
        return _exclusions.ContainsKey(deviceIndex);
    }

    /// <summary>
    /// Removes all exclusion entries. Called when a pipeline run completes.
    /// </summary>
    public void ClearRunExclusions()
    {
        _exclusions.Clear();
    }

    private enum ExclusionReason
    {
        MemoryExhausted,
        Failed
    }
}
