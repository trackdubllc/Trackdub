namespace Trackdub.Contracts.Benchmarking;

/// <summary>
/// Reports free VRAM on a compute adapter.
/// </summary>
/// <remarks>
/// This is adapter-wide headroom, not this process's GPU allocation: DXGI's
/// <c>QueryVideoMemoryInfo</c> reports budget minus current usage for the whole adapter, so the
/// value also moves with other processes on the same GPU. Use it to detect memory pressure, not
/// to attribute bytes to a stage. Returns null where the platform cannot report VRAM.
/// </remarks>
public interface IAvailableVramReader
{
    /// <summary>Free VRAM in MB, or null when this platform or device cannot report it.</summary>
    long? ReadAvailableVramMb();

    /// <summary>Why the reading is unavailable, surfaced verbatim in benchmark evidence.</summary>
    string UnavailableReason { get; }
}
