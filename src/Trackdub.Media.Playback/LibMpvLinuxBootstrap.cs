using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Trackdub.Media.Playback;

/// <summary>
/// Startup check that verifies libmpv is available on the system on Linux.
/// Unlike Windows/macOS, there is no auto-download; libmpv must be installed
/// via the system package manager (apt, dnf, pacman, etc.).
/// </summary>
public static class LibMpvLinuxBootstrap
{
    private static readonly Lock Gate = new();
    private static bool Attempted;

    public static void TryEnsureIfManifestPresent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        lock (Gate)
        {
            if (Attempted)
            {
                return;
            }

            Attempted = true;
        }

        try
        {
            TryEnsureCore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Trace.TraceWarning($"LibMpv Linux check failed: {ex.Message}");
        }
    }

    private static void TryEnsureCore()
    {
        if (IsLibMpvAvailable())
        {
            return;
        }

        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "linux-arm64"
            : "linux-x64";

        Trace.TraceWarning(
            $"libmpv was not found on this system (checked common library paths for {rid}). " +
            "Video playback will be unavailable. " +
            "Install libmpv using your distribution's package manager:\n" +
            "  Debian/Ubuntu:  sudo apt install libmpv2\n" +
            "  Fedora/RHEL:    sudo dnf install mpv-libs\n" +
            "  Arch Linux:     sudo pacman -S mpv\n" +
            "  openSUSE:       sudo zypper install libmpv2\n" +
            "Alternatively, set TRACKDUB_LIBMPV_PATH to the full path of the library.");
    }

    private static bool IsLibMpvAvailable()
    {
        foreach (string path in EnumerateSystemCandidates())
        {
            if (File.Exists(path))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateSystemCandidates()
    {
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "aarch64-linux-gnu"
            : "x86_64-linux-gnu";

        string[] libNames = ["libmpv.so.2", "libmpv.so.1", "libmpv.so"];
        string[] libDirs =
        [
            $"/usr/lib/{arch}",
            "/usr/lib",
            "/usr/local/lib",
            $"/usr/lib64",
            "/lib",
            $"/lib/{arch}",
        ];

        foreach (string dir in libDirs)
        {
            foreach (string name in libNames)
            {
                yield return Path.Combine(dir, name);
            }
        }

        // Also honour XDG_DATA_HOME / user-local installs
        string? home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            foreach (string name in libNames)
            {
                yield return Path.Combine(home, ".local", "lib", name);
                yield return Path.Combine(home, ".local", "lib64", name);
            }
        }
    }
}
