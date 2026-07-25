using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Trackdub.Licensing;

/// <summary>
/// Extracts the IOPlatformUUID from macOS ioreg output.
/// Runs: ioreg -rd1 -c IOPlatformExpertDevice
/// </summary>
internal sealed partial class MacOsFingerprintSource : IFingerprintSource
{
    public string GetRawMachineId()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "ioreg",
            Arguments = "-rd1 -c IOPlatformExpertDevice",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ioreg exited with code {process.ExitCode}.");
        }

        var match = UuidRegex().Match(output);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "IOPlatformUUID not found in ioreg output.");
        }

        return match.Groups[1].Value;
    }

    [GeneratedRegex("\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"")]
    private static partial Regex UuidRegex();
}
