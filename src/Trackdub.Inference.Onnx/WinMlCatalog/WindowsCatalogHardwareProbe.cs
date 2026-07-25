using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Trackdub.Inference.Onnx.WinMlCatalog;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsCatalogHardwareProbe
{
    public static bool ProcessorNameContains(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string? processor = TryReadProcessorName();
        return processor is not null &&
               processor.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    public static bool DisplayAdapterProviderContains(string providerToken)
    {
        if (string.IsNullOrWhiteSpace(providerToken))
        {
            return false;
        }

        return RegistryDisplayClassContains(deviceKey =>
        {
            string? provider = deviceKey.GetValue("ProviderName") as string;
            return provider is not null &&
                   provider.Contains(providerToken, StringComparison.OrdinalIgnoreCase);
        });
    }

    public static bool RegistryDisplayClassContains(Func<RegistryKey, bool> deviceMatches)
    {
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
                if (deviceKey is not null && deviceMatches(deviceKey))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool DeviceDescriptionContains(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // Check DriverDesc and DeviceDesc (both are used by different GPU drivers)
        // as well as the CPU processor name string.
        return RegistryDisplayClassContains(deviceKey =>
        {
            string? driverDesc = deviceKey.GetValue("DriverDesc") as string;
            if (driverDesc is not null && driverDesc.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;

            string? deviceDesc = deviceKey.GetValue("DeviceDesc") as string;
            return deviceDesc is not null &&
                   deviceDesc.Contains(token, StringComparison.OrdinalIgnoreCase);
        }) || ProcessorNameContains(token);
    }

    public static string? TryReadProcessorName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void ForEachIntelDisplayAdapter(Action<string?, string?> visit)
    {
        RegistryDisplayClassContains(deviceKey =>
        {
            string? provider = deviceKey.GetValue("ProviderName") as string;
            if (provider is null || !provider.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? driverDesc = deviceKey.GetValue("DriverDesc") as string;
            string? deviceDesc = deviceKey.GetValue("DeviceDesc") as string;
            visit(driverDesc, deviceDesc);
            return false;
        });
    }

    public static bool HasIntelNpuDevice()
    {
        try
        {
            using RegistryKey? pciRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pciRoot is null)
            {
                return false;
            }

            foreach (string deviceId in pciRoot.GetSubKeyNames())
            {
                using RegistryKey? deviceKey = pciRoot.OpenSubKey(deviceId);
                if (deviceKey is null)
                {
                    continue;
                }

                foreach (string instanceId in deviceKey.GetSubKeyNames())
                {
                    using RegistryKey? instanceKey = deviceKey.OpenSubKey(instanceId);
                    if (instanceKey is null)
                    {
                        continue;
                    }

                    string details = string.Join(
                        ' ',
                        GetRegistryString(instanceKey, "FriendlyName"),
                        GetRegistryString(instanceKey, "DeviceDesc"),
                        GetRegistryString(instanceKey, "Mfg"),
                        GetRegistryString(instanceKey, "Service"),
                        GetRegistryString(instanceKey, "Class"));

                    bool looksLikeNpu = ContainsAny(
                        details,
                        "npu",
                        "neural",
                        "ai boost",
                        "intelnpu");

                    bool isIntelDevice = details.Contains("intel", StringComparison.OrdinalIgnoreCase) ||
                                         deviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase);

                    if (looksLikeNpu && isIntelDevice)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string GetRegistryString(RegistryKey key, string valueName) =>
        key.GetValue(valueName) as string ?? string.Empty;

    private static bool ContainsAny(string value, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
