using System.Runtime.InteropServices;

namespace Trackdub.Media.Playback;

/// <summary>
/// Resolves the path to the bundled libvlc runtime directory.
/// </summary>
public interface ILibVlcRuntimeLocator
{
    /// <summary>
    /// Resolves the path to the bundled libvlc runtime directory.
    /// Returns null if the runtime is not found or is malformed.
    /// </summary>
    string? ResolveRuntimePath();
}

/// <summary>
/// Probes for a bundled <c>libvlc</c> subdirectory relative to a configurable base directory
/// and validates the presence of a platform-appropriate native library file.
/// Falls back to well-known system library paths on Linux.
/// </summary>
public sealed class LibVlcRuntimeLocator : ILibVlcRuntimeLocator
{
    private readonly string baseDirectory;

    /// <summary>
    /// Creates a new runtime locator that probes relative to <paramref name="baseDirectory"/>.
    /// When null, defaults to <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public LibVlcRuntimeLocator(string? baseDirectory = null)
    {
        this.baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    }

    /// <inheritdoc />
    public string? ResolveRuntimePath()
    {
        string rid = GetCurrentRuntimeIdentifier();
        foreach (string candidate in EnumerateCandidateDirectories(rid))
        {
            if (Directory.Exists(candidate) && HasPlatformLibrary(candidate))
            {
                return candidate;
            }

            string? archSubfolder = ProbeArchitectureSubfolders(candidate, rid);
            if (archSubfolder is not null)
            {
                return archSubfolder;
            }
        }

        // On Linux, probe system-installed libvlc paths
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string? systemPath = ProbeLinuxSystemPaths();
            if (systemPath is not null)
            {
                return systemPath;
            }
        }

        return null;
    }

    private static string GetCurrentRuntimeIdentifier()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 when OperatingSystem.IsWindows() => "win-arm64",
            Architecture.Arm64 when OperatingSystem.IsMacOS() => "osx-arm64",
            Architecture.Arm64 => "linux-arm64",
            _ when OperatingSystem.IsWindows() => "win-x64",
            _ when OperatingSystem.IsMacOS() => "osx-x64",
            _ => "linux-x64",
        };
    }

    private IEnumerable<string> EnumerateCandidateDirectories(string rid)
    {
        yield return Path.Combine(baseDirectory, "libvlc");

        int depth = 0;
        for (string? current = baseDirectory; !string.IsNullOrWhiteSpace(current) && depth < 14; depth++)
        {
            yield return Path.Combine(current, "native", rid, "libvlc");
            yield return Path.Combine(current, "libvlc", rid);
            current = Directory.GetParent(current)?.FullName;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Trackdub", "native", rid, "libvlc");
            yield return Path.Combine(localAppData, "Trackdub", "libvlc", rid);
        }
    }

    private static string? ProbeArchitectureSubfolders(string candidate, string rid)
    {
        // Bundled runtime packages lay out every architecture side by side
        // (libvlc/win-x64, libvlc/win-arm64, ...); the subfolder matching the
        // current process architecture must win over arbitrary enumeration order.
        string ridFolder = Path.Combine(candidate, rid);
        if (Directory.Exists(ridFolder) && HasPlatformLibrary(ridFolder))
        {
            return ridFolder;
        }

        try
        {
            foreach (string subdirectory in Directory.EnumerateDirectories(candidate))
            {
                if (HasPlatformLibrary(subdirectory))
                {
                    return subdirectory;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Directory access issues; skip probing
        }

        return null;
    }

    private static string? ProbeLinuxSystemPaths()
    {
        string[] systemPaths =
        [
            "/usr/lib",
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib/aarch64-linux-gnu",
            "/usr/lib64",
        ];

        foreach (string path in systemPaths)
        {
            if (Directory.Exists(path) && HasLibVlcSo(path))
            {
                return path;
            }
        }

        return null;
    }

    private static bool HasLibVlcSo(string directory)
    {
        if (File.Exists(Path.Combine(directory, "libvlc.so")))
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "libvlc.so.*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasPlatformLibrary(string directory)
    {
        if (File.Exists(Path.Combine(directory, "libvlc.dll"))
            || File.Exists(Path.Combine(directory, "libvlc.so"))
            || File.Exists(Path.Combine(directory, "libvlc.dylib")))
        {
            return true;
        }

        // Check for versioned sonames on Linux (e.g. libvlc.so.5, libvlc.so.5.6.0)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                return Directory.EnumerateFiles(directory, "libvlc.so.*").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }
}
