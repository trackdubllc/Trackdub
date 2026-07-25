using System.Runtime.Versioning;
using Microsoft.Win32;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsNvidiaHardwareGate
{
    public static (bool Eligible, TensorRtRtxReadinessBlocker Blocker, string Detail) Evaluate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, TensorRtRtxReadinessBlocker.PlatformUnsupported, "TensorRT RTX EP ABI plugin route is Windows-only.");
        }

        if (!TryGetPrimaryNvidiaAdapter(out string? adapterName, out string? driverVersion))
        {
            return (false, TensorRtRtxReadinessBlocker.GpuVendorMismatch, "No NVIDIA GPU detected for TensorRT RTX.");
        }

        string driver = string.IsNullOrWhiteSpace(driverVersion) ? "unknown" : driverVersion;
        return (true, TensorRtRtxReadinessBlocker.None, $"NVIDIA GPU '{adapterName}' detected (driver {driver}).");
    }

    private static bool TryGetPrimaryNvidiaAdapter(out string? adapterName, out string? driverVersion)
    {
        adapterName = null;
        driverVersion = null;

        try
        {
            const string displayClassGuid = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(displayClassGuid);
            if (classKey is null)
            {
                return false;
            }

            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _))
                {
                    continue;
                }

                using RegistryKey? deviceKey = classKey.OpenSubKey(subKeyName);
                if (deviceKey is null)
                {
                    continue;
                }

                string? provider = deviceKey.GetValue("ProviderName") as string;
                string? desc = deviceKey.GetValue("DriverDesc") as string;
                if (provider is null || !provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                adapterName = desc ?? provider;
                driverVersion = deviceKey.GetValue("DriverVersion") as string;
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
