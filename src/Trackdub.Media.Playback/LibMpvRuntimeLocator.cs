using System.Runtime.InteropServices;

namespace Trackdub.Media.Playback;

/// <summary>
/// Resolves the path to the bundled libmpv runtime library.
/// </summary>
public interface ILibMpvRuntimeLocator
{
    /// <summary>
    /// Resolves the full path to the libmpv runtime library, or null if it is unavailable.
    /// </summary>
    string? ResolveRuntimeLibraryPath();
}

/// <summary>
/// Probes for a bundled libmpv native library relative to a configurable base directory.
/// </summary>
public sealed class LibMpvRuntimeLocator(string? baseDirectory = null) : ILibMpvRuntimeLocator
{
    private static readonly string[] WindowsLibraryNames =
    [
        "libmpv-2.dll",
        "libmpv-1.dll",
        "mpv-2.dll",
        "mpv-1.dll",
    ];

    private static readonly string[] MacLibraryNames =
    [
        "libmpv.2.dylib",
        "libmpv.1.dylib",
        "libmpv.dylib",
    ];

    private static readonly string[] LinuxLibraryNames =
    [
        "libmpv.so.2",
        "libmpv.so.1",
        "libmpv.so",
    ];

    private readonly string baseDirectory = baseDirectory ?? AppContext.BaseDirectory;

    public string? ResolveRuntimeLibraryPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return ResolveFromCandidates(EnumerateWindowsCandidatePaths());
        }

        if (OperatingSystem.IsMacOS())
        {
            return ResolveFromCandidates(EnumerateMacCandidatePaths());
        }

        if (OperatingSystem.IsLinux())
        {
            return ResolveFromCandidates(EnumerateLinuxCandidatePaths());
        }

        return null;
    }

    private string? ResolveFromCandidates(IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateWindowsCandidatePaths()
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };

        foreach (string path in EnumerateNativeRootPaths(rid, WindowsLibraryNames))
        {
            yield return path;
        }
    }

    private IEnumerable<string> EnumerateMacCandidatePaths()
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "osx-arm64",
            _ => "osx-x64",
        };

        foreach (string path in EnumerateNativeRootPaths(rid, MacLibraryNames))
        {
            yield return path;
        }
    }

    private IEnumerable<string> EnumerateLinuxCandidatePaths()
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "linux-arm64",
            _ => "linux-x64",
        };

        foreach (string path in EnumerateNativeRootPaths(rid, LinuxLibraryNames))
        {
            yield return path;
        }
    }

    private IEnumerable<string> EnumerateNativeRootPaths(string rid, IReadOnlyList<string> libraryNames)
    {
        string safeRid = Path.GetFileName(rid);
        if (string.IsNullOrWhiteSpace(safeRid) || !string.Equals(safeRid, rid, StringComparison.Ordinal))
        {
            yield break;
        }

        // Prefer libraries shipped next to the app (publish / bin output) before user-profile bootstrap
        // downloads. A stale or partial %LocalAppData% copy must not override a good bundled runtime.
        foreach (string path in EnumerateBaseDirectoryNativePaths(safeRid, libraryNames))
        {
            yield return path;
        }

        string? appSupport = OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Trackdub",
                "native",
                safeRid)
            : null;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            foreach (string libraryName in libraryNames)
            {
                yield return Path.Combine(localAppData, "Trackdub", "native", safeRid, libraryName);
            }
        }

        if (!string.IsNullOrWhiteSpace(appSupport))
        {
            foreach (string libraryName in libraryNames)
            {
                yield return Path.Combine(appSupport, libraryName);
            }
        }
    }

    private IEnumerable<string> EnumerateBaseDirectoryNativePaths(string safeRid, IReadOnlyList<string> libraryNames)
    {
        int depth = 0;
        for (string? current = baseDirectory; !string.IsNullOrWhiteSpace(current) && depth < 14; depth++)
        {
            foreach (string libraryName in libraryNames)
            {
                yield return Path.Combine(current, "native", safeRid, libraryName);
                yield return Path.Combine(current, libraryName);
            }

            current = Directory.GetParent(current)?.FullName;
        }
    }
}
