namespace Trackdub.Domain.Benchmarking;

/// <summary>Optional inclusive limits; null imposes no budget on an available metric.</summary>
public sealed record ResourceTelemetryBounds
{
    public double? MaxCpuPercent { get; init; }
    public long? MaxWorkingSetBytes { get; init; }
    public long? MaxManagedAllocatedBytes { get; init; }

    /// <summary>
    /// Inclusive floor on free VRAM in MB. Breaching it means the run came under GPU memory
    /// pressure. This is a minimum, not a maximum: a run that leaves less headroom fails.
    /// </summary>
    public long? MinAvailableVramMb { get; init; }

    /// <summary>
    /// Inclusive ceiling on this process's dedicated GPU memory in bytes (the <c>gpuBytes</c>
    /// metric). Unlike <see cref="MinAvailableVramMb"/> this is a usage maximum on a
    /// process-isolated reading, so breaching it means this run allocated more of the adapter's
    /// own memory than it was allowed, regardless of what other processes are doing.
    /// </summary>
    public long? MaxGpuBytes { get; init; }
}
