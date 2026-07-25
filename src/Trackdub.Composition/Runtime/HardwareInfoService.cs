using System.Diagnostics;
using Trackdub.Application.Runtime;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Runtime;

internal sealed class HardwareInfoService(
    IHardwareProfileProvider provider,
    IDeviceEnumerator? deviceEnumerator = null) : IHardwareInfoService
{
    public async Task<HardwareDisplaySummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        HardwareProfile profile = await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        string cpuName = !string.IsNullOrWhiteSpace(profile.CpuName) ? profile.CpuName : "Unknown CPU";
        string gpuName = !string.IsNullOrWhiteSpace(profile.GpuDescription) ? profile.GpuDescription : "Unknown GPU";
        string ramDisplay = profile.TotalRamMb > 0 ? FormatMb(profile.TotalRamMb) + " RAM" : "RAM unknown";

        long vramMb = profile.DedicatedVramMb;

        // Registry VRAM can be unreliable on modern GPU drivers; fall back to DXGI via IDeviceEnumerator
        if (vramMb <= 0 && deviceEnumerator is not null)
        {
            try
            {
                IReadOnlyList<DeviceEntry> devices =
                    await deviceEnumerator.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
                DeviceEntry? gpu = devices.FirstOrDefault(d =>
                    d.Kind is DeviceKind.DiscreteGpu or DeviceKind.IntegratedGpu);
                if (gpu is not null)
                    vramMb = gpu.DedicatedVramMb;
            }
            catch (Exception ex) { Debug.WriteLine($"[HardwareInfoService] Failed to enumerate GPU devices: {ex.Message}"); }
        }

        string vramDisplay = vramMb > 0 ? FormatMb(vramMb) + " VRAM" : "VRAM unknown";

        return new HardwareDisplaySummary(cpuName, gpuName, vramDisplay, ramDisplay);
    }

    private static string FormatMb(long mb) =>
        mb >= 1024 ? $"{(double)mb / 1024:F0} GB" : $"{mb} MB";
}
