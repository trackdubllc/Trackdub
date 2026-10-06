using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Infrastructure.Diagnostics;

/// <summary>
/// Fallback used when a host has not registered a platform process-GPU source. Telemetry records
/// an explicit unavailable result with a reason rather than fabricating a zero reading.
/// </summary>
public sealed class UnavailableProcessGpuMemoryReader : IProcessGpuMemoryReader
{
    public long? ReadDedicatedGpuMemoryBytes() => null;

    public IReadOnlyDictionary<int, long>? ReadDedicatedGpuMemoryBytesByAdapter() => null;

    public string UnavailableReason => "No platform process GPU memory source is available for this host.";
}
