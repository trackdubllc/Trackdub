using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Infrastructure.Diagnostics;

/// <summary>
/// Fallback used when a host has not registered a platform VRAM source. Telemetry records an
/// explicit unavailable result with a reason rather than fabricating a zero reading.
/// </summary>
public sealed class UnavailableAvailableVramReader : IAvailableVramReader
{
    public long? ReadAvailableVramMb() => null;

    public string UnavailableReason => "No VRAM reader is registered for this host.";
}
