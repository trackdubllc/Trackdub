using System.Runtime.Versioning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.WinMlCatalog;

namespace Trackdub.Inference.Onnx.WinMlCatalog;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsAmdNpuCatalogHardwareGate
{
    public static (bool Eligible, WinMlCatalogReadinessBlocker Blocker, string Detail) Evaluate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, WinMlCatalogReadinessBlocker.PlatformUnsupported, "VitisAI catalog route is Windows-only.");
        }

        bool amd = WindowsCatalogHardwareProbe.DisplayAdapterProviderContains("AMD") ||
                   WindowsCatalogHardwareProbe.DeviceDescriptionContains("AMD");
        bool npu = WindowsCatalogHardwareProbe.DeviceDescriptionContains("XDNA") ||
                   WindowsCatalogHardwareProbe.DeviceDescriptionContains("Ryzen AI") ||
                   WindowsCatalogHardwareProbe.DeviceDescriptionContains("NPU");
        if (!amd || !npu)
        {
            return (
                false,
                WinMlCatalogReadinessBlocker.HardwareNotSupported,
                VitisAiProviderConstants.HardwareNotSupportedDetail);
        }

        return (true, WinMlCatalogReadinessBlocker.None, "AMD Ryzen AI (XDNA) NPU hardware detected for VitisAI catalog EP.");
    }
}
