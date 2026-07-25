using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Trackdub.Benchmarks;

/// <summary>
/// Captures stable hardware identifiers for benchmark reports.
/// Cross-platform: Windows uses WMIC; Linux reads /proc/cpuinfo.
/// </summary>
internal static class BenchmarkHardwareInfo
{
    public static string Capture()
    {
        var parts = new List<string>
        {
            $"CPU: {GetCpuName()}",
            $"RAM: {GetTotalMemoryGB()} GB",
            $"OS: {GetOsVersion()}",
            $"GPU: {GetGpuName()}",
        };

        return string.Join(" | ", parts);
    }

    private static string GetCpuName()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetWmiProperty("Win32_Processor", "Name");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in File.ReadAllLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = line.IndexOf(':');
                        if (idx >= 0)
                        {
                            string val = line.Substring(idx + 1).Trim();
                            // Strip surrounding quotes.
                            if (val.Length >= 2 &&
                                ((val[0] == '"' && val[^1] == '"') ||
                                 (val[0] == '\'' && val[^1] == '\'')))
                                val = val[1..^1];
                            return val;
                        }
                    }
                }
            }

            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
                ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")
                ?? "Unknown";
        }
        catch (Exception)
        {
            // CPU detection failed - return Unknown to allow benchmark to continue
            return "Unknown";
        }
    }

    private static string GetGpuName()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetWmiProperty("Win32_VideoController", "Name");
        }
        catch (Exception)
        {
            // GPU detection failed - return Unknown to allow benchmark to continue
            return "Unknown";
        }

        return "Unknown";
    }

    private static long GetTotalMemoryGB()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            return info.TotalAvailableMemoryBytes / 1024 / 1024 / 1024;
        }
        catch (Exception)
        {
            // Memory detection failed - return 0 to allow benchmark to continue
            return 0;
        }
    }

    private static string GetOsVersion()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"Windows {Environment.OSVersion.Version}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macOS";
        return Environment.OSVersion.ToString();
    }

    /// <summary>
    /// Uses WMIC to query a WMI property on Windows.
    /// Returns "Unknown" on failure.
    /// </summary>
    private static string GetWmiProperty(string className, string propertyName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = $"path {className} get {propertyName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return "Unknown";

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException)
                {
                    // Best-effort cleanup after timeout; process may have already exited.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Best-effort cleanup after timeout; ignore kill failure and continue returning Unknown.
                }
                return "Unknown";
            }

            string output = process.StandardOutput.ReadToEnd();

            // Skip header line; return first non-empty result.
            return output.Split('\n')
                .Skip(1)
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim()
                ?? "Unknown";
        }
        catch (Exception)
        {
            // WMI property query failed - return Unknown to allow benchmark to continue
            return "Unknown";
        }
    }
}
