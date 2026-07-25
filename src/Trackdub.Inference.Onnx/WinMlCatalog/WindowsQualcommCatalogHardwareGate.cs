using System.Runtime.Versioning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.WinMlCatalog;

namespace Trackdub.Inference.Onnx.WinMlCatalog;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsQualcommCatalogHardwareGate
{
    public static (bool Eligible, WinMlCatalogReadinessBlocker Blocker, string Detail) Evaluate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, WinMlCatalogReadinessBlocker.PlatformUnsupported, "QNN catalog route is Windows-only.");
        }

        bool qualcomm = WindowsCatalogHardwareProbe.DeviceDescriptionContains("Qualcomm") ||
                        WindowsCatalogHardwareProbe.DeviceDescriptionContains("Hexagon") ||
                        WindowsCatalogHardwareProbe.ProcessorNameContains("Snapdragon");
        if (!qualcomm)
        {
            return (
                false,
                WinMlCatalogReadinessBlocker.HardwareNotSupported,
                QnnProviderConstants.HardwareNotSupportedDetail);
        }

        return (true, WinMlCatalogReadinessBlocker.None, "Qualcomm / Snapdragon hardware detected for QNN catalog EP.");
    }
}
