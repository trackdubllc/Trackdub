using System.Runtime.Versioning;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Runtime;

/// <summary>
/// Adapts the existing DXGI-backed <see cref="IVramMonitor"/> to the telemetry port.
/// </summary>
/// <remarks>
/// <para>
/// The reading is adapter-wide headroom, so it moves with other processes on the same GPU.
/// It is evidence of memory pressure, not a per-stage byte attribution.
/// </para>
/// <para>
/// <see cref="DeviceIndex"/> is recorded in the run's configuration so a reader can tell which
/// adapter the number came from.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAvailableVramReader(IVramMonitor monitor, int deviceIndex = 0)
    : IAvailableVramReader
{
    /// <summary>Adapter the reading was taken from, for inclusion in evidence configuration.</summary>
    public int DeviceIndex { get; } = deviceIndex;

    public long? ReadAvailableVramMb() => monitor.QueryAvailableVramMb(DeviceIndex);

    public string UnavailableReason =>
        $"Windows DXGI reported no free VRAM for device {DeviceIndex}.";
}
