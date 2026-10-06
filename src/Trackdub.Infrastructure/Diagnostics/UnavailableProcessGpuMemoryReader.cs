using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Infrastructure.Diagnostics;

/// <summary>
/// Fallback used when a host has not registered a platform process-GPU source. Telemetry records
/// an explicit unavailable result with a reason rather than fabricating a zero reading.
/// </summary>
public sealed class UnavailableProcessGpuMemoryReader : IProcessGpuMemoryReader
{
    public long? ReadDedicatedGpuMemoryBytes() => null;

    public string UnavailableReason => "No process GPU memory reader is registered for this host.";
}
