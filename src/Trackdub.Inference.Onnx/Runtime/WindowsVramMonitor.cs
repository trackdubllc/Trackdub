using System.Runtime.Versioning;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime;

/// <summary>
/// Windows implementation of <see cref="IVramMonitor"/> that queries live VRAM budget
/// via IDXGIAdapter3::QueryVideoMemoryInfo.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVramMonitor(IDeviceEnumerator deviceEnumerator) : IVramMonitor
{
    public long? QueryAvailableVramMb(int deviceIndex)
    {
        // Best-effort: if the enumerator hasn't run yet, return null rather than blocking.
        IReadOnlyList<DeviceEntry>? devices = null;
        try
        {
            // GetDevicesAsync with no cancellation -- use a cached result if available.
            // We call .GetAwaiter().GetResult() only because this is a diagnostic path
            // (not on a hot inference path) and the underlying implementation caches results.
            devices = deviceEnumerator.GetDevicesAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }

        DeviceEntry? device = devices.FirstOrDefault(d => d.DeviceIndex == deviceIndex);
        if (device is null || device.AdapterLuid is null)
            return null;

        try
        {
            return WindowsDeviceEnumerator.QueryAvailableVramMb(device.AdapterLuid.Value);
        }
        catch
        {
            return null;
        }
    }
}
