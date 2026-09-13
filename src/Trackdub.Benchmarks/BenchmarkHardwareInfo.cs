using System.ComponentModel;
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
        // wmic is deprecated and absent on modern Windows images (for example
        // windows-latest CI runners). Skip the spawn entirely when it cannot be
        // resolved so we never leave an orphaned child process or an undrained
        // redirected-pipe reader thread alive: such a lingering foreground thread
        // keeps the test host from exiting cleanly and corrupts the xunit.v3
        // stdout IPC handshake that dotnet test uses to enumerate assemblies.
        if (TryResolveExecutable("wmic") is not { } wmicPath)
        {
            return "Unknown";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = wmicPath,
                Arguments = $"path {className} get {propertyName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return "Unknown";

            // Drain both redirected pipes on background reader tasks BEFORE waiting.
            // Reading stdout to completion before WaitForExit (the previous ordering)
            // can deadlock when the child fills the stderr pipe buffer, and leaves the
            // stderr reader undrained. Draining both first guarantees the process ends
            // and its reader threads complete, so nothing keeps the host alive.
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                KillProcessTreeBestEffort(process);
                WaitForExitBestEffort(process, 5000);
                Task.WaitAll([outputTask, errorTask], 5000);
                return "Unknown";
            }

            // Ensure the async pipe readers have fully completed (and their threads
            // released) before the process handle is disposed by the using block.
            Task.WaitAll([outputTask, errorTask], 5000);
            if (!outputTask.IsCompletedSuccessfully || !errorTask.IsCompletedSuccessfully)
            {
                return "Unknown";
            }

            string output = outputTask.Result;

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

    /// <summary>
    /// Returns the full path when <paramref name="executableName"/> can be located
    /// under Windows System32\wbem, System32, or PATH. Used to avoid spawning tools
    /// such as <c>wmic</c> that have been removed from modern Windows images, and
    /// to start the same path that the probe found (wbem is often not on PATH).
    /// </summary>
    private static string? TryResolveExecutable(string executableName)
    {
        try
        {
            string safeName = Path.GetFileName(executableName);
            if (string.IsNullOrWhiteSpace(safeName) || Path.IsPathRooted(safeName))
            {
                return null;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string wbemCandidate = Path.Combine(systemDirectory, "wbem", safeName + ".exe");
                if (File.Exists(wbemCandidate))
                {
                    return wbemCandidate;
                }

                string systemCandidate = Path.Combine(systemDirectory, safeName + ".exe");
                if (File.Exists(systemCandidate))
                {
                    return systemCandidate;
                }
            }

            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVariable))
            {
                return null;
            }

            string[] extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? [".exe", ".cmd", ".bat", string.Empty]
                : [string.Empty];

            foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string extension in extensions)
                {
                    string candidateName = Path.GetFileName(safeName + extension);
                    if (string.IsNullOrEmpty(candidateName) || Path.IsPathRooted(candidateName))
                    {
                        continue;
                    }

                    string candidate = Path.Combine(directory, candidateName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            // Filesystem/PATH probe failed. Treat the executable as missing rather
            // than spawn a process that cannot be cleaned up deterministically.
            return null;
        }
    }

    private static void KillProcessTreeBestEffort(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        catch (Win32Exception)
        {
            // Best-effort cleanup after timeout.
        }
        catch (NotSupportedException)
        {
            // Entire-tree kill is unsupported on this platform.
        }
    }

    private static void WaitForExitBestEffort(Process process, int milliseconds)
    {
        try
        {
            process.WaitForExit(milliseconds);
        }
        catch (InvalidOperationException)
        {
            // Process was never started or has already been disposed.
        }
        catch (Win32Exception)
        {
            // Wait failed; callers still drain redirected pipes before dispose.
        }
    }
}
