namespace Trackdub.Domain.Benchmarking;

/// <summary>
/// Process-wide counters, including concurrent work but excluding child processes.
/// WorkingSetBytes is a point-in-time sample; PeakWorkingSetBytes is populated by interval samplers.
/// </summary>
public sealed record ResourceUsageSnapshot
{
    public double? CpuTimeMilliseconds { get; init; }
    public double? MonotonicMilliseconds { get; init; }
    public int ProcessorCount { get; init; }
    public long? WorkingSetBytes { get; init; }

    /// <summary>Maximum working set observed during a measured interval, when interval sampling ran.</summary>
    public long? PeakWorkingSetBytes { get; init; }
    public long? ManagedAllocatedBytes { get; init; }
    public string? PeakWorkingSetUnavailableReason { get; init; }

    /// <summary>
    /// Free VRAM on the sampled adapter, in MB. Adapter-wide headroom, not this process's
    /// allocation: DXGI reports budget minus current usage for the whole adapter (ADR-0009).
    /// </summary>
    public long? AvailableVramMb { get; init; }

    public string? CpuUnavailableReason { get; init; }
    public string? MemoryUnavailableReason { get; init; }
    public string? VramUnavailableReason { get; init; }
}
