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
}
