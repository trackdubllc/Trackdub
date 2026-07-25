using System.Runtime.Versioning;
using Microsoft.Win32;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Inference.Onnx.Migraphx;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsMigraphxHardwareGate
{
    public static (bool Eligible, MigraphxReadinessBlocker Blocker, string Detail) Evaluate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, MigraphxReadinessBlocker.PlatformUnsupported, "MIGraphX WinML route is Windows-only.");
        }

        int build = Environment.OSVersion.Version.Build;
        if (build < MigraphxProviderConstants.WindowsMinimumBuild)
        {
            return (
                false,
                MigraphxReadinessBlocker.OsVersionUnsupported,
                $"Windows build {build} is below required {MigraphxProviderConstants.WindowsMinimumBuild} (Windows 11 24H2+).");
        }

        if (!TryGetPrimaryAmdAdapter(out string? adapterName, out string? driverVersion))
        {
            return (false, MigraphxReadinessBlocker.GpuVendorMismatch, "No AMD discrete GPU detected for MIGraphX.");
        }

        if (!string.Equals(
                NormalizeDriverVersion(driverVersion),
                MigraphxProviderConstants.WindowsRequiredAmdDriverVersion,
                StringComparison.Ordinal))
        {
            return (
                false,
                MigraphxReadinessBlocker.DriverVersionMismatch,
                $"AMD driver '{driverVersion ?? "unknown"}' on '{adapterName}' does not match required " +
                $"{MigraphxProviderConstants.WindowsRequiredAmdDriverVersion}.");
        }

        return (true, MigraphxReadinessBlocker.None, $"AMD GPU '{adapterName}' meets MIGraphX hardware gates.");
    }

    private static bool TryGetPrimaryAmdAdapter(out string? adapterName, out string? driverVersion)
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
                if (provider is null || !provider.Contains("AMD", StringComparison.OrdinalIgnoreCase))
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

    private static string NormalizeDriverVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        string trimmed = version.Trim();
        int comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            trimmed = trimmed[..comma];
        }

        return trimmed;
    }
}
