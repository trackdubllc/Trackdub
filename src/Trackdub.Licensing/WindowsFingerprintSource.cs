using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Trackdub.Licensing;

/// <summary>
/// Reads the Windows Machine GUID from the registry.
/// Path: HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFingerprintSource : IFingerprintSource
{
    public string GetRawMachineId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var guid = key?.GetValue("MachineGuid") as string;

        if (string.IsNullOrWhiteSpace(guid))
        {
            throw new InvalidOperationException(
                "Windows MachineGuid registry value not found or empty.");
        }

        return guid;
    }
}
