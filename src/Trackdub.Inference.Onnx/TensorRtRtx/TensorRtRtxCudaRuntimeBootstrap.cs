using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>Result of probing/loading the CUDA runtime required by the TRT RTX EP ABI plugin.</summary>
internal sealed record TensorRtRtxCudaRuntimeEnsureResult(
    bool Succeeded,
    string? LoadedPath,
    string Detail)
{
    public static TensorRtRtxCudaRuntimeEnsureResult Failed(string detail) =>
        new(false, null, detail);
}

/// <summary>
/// Ensures the CUDA runtime needed by the pinned cu13 TRT RTX EP ABI bundle is visible before ORT loads
/// the plugin. On Windows the TensorRT-RTX 1.6 cu13 binaries link the CUDA runtime statically (no
/// cudart import), so nothing is required. On Linux the bundle ships libcudart.so.13 beside the plugin.
/// </summary>
internal static class TensorRtRtxCudaRuntimeBootstrap
{
    internal const string LinuxCudaRuntimeFileName = "libcudart.so.13";
    internal const int RequiredCudaMajorVersion = 13;
    private const string CudaRuntimeEnvironmentVariable = TensorRtRtxProviderConstants.CudaRuntimeBinDirectoryEnvironmentVariable;

    /// <summary>Shared CUDA runtime library the plugin needs at load, or <see langword="null"/> when statically linked.</summary>
    internal static string? RequiredCudaRuntimeFileName =>
        OperatingSystem.IsWindows() ? null : LinuxCudaRuntimeFileName;

    public static TensorRtRtxCudaRuntimeEnsureResult TryEnsureLoadedResult(string? pluginDirectory = null)
    {
        string? runtimeFileName = RequiredCudaRuntimeFileName;
        if (runtimeFileName is null)
        {
            return new TensorRtRtxCudaRuntimeEnsureResult(
                true,
                null,
                $"CUDA runtime is statically linked into the TensorRT-RTX {TensorRtRtxProviderConstants.BundledTrtRtxRuntimeVersion} "
                + $"{TensorRtRtxProviderConstants.BundledCudaVariant} bundle; no CUDA runtime library is required.");
        }

        return TryEnsureLoadedResult(DiscoverSearchDirectories(pluginDirectory), runtimeFileName);
    }

    internal static TensorRtRtxCudaRuntimeEnsureResult TryEnsureLoadedResult(
        IEnumerable<string> searchDirectories,
        string runtimeFileName)
    {
        foreach (string directory in searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string candidatePath = Path.Join(directory, runtimeFileName);
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            PrependProcessPath(directory);
            if (TryLoadNativeLibrary(candidatePath))
            {
                return new TensorRtRtxCudaRuntimeEnsureResult(
                    true,
                    candidatePath,
                    $"CUDA {RequiredCudaMajorVersion} runtime loaded from '{candidatePath}'.");
            }

            return TensorRtRtxCudaRuntimeEnsureResult.Failed(
                $"CUDA {RequiredCudaMajorVersion} runtime file exists at '{candidatePath}' but failed to load. "
                + "Inspect its dependencies or reinstall the TensorRT RTX EP bundle.");
        }

        string installedCudaHint = DescribeInstalledCudaMajorVersions();
        return TensorRtRtxCudaRuntimeEnsureResult.Failed(
            $"CUDA {RequiredCudaMajorVersion} runtime ({runtimeFileName}) was not found beside the TensorRT RTX plugin "
            + $"or in configured search paths. The {TensorRtRtxProviderConstants.BundledCudaVariant} bundle ships it; "
            + "reinstall the bundle from Model Manager. "
            + installedCudaHint
            + $" To use a system CUDA {RequiredCudaMajorVersion} runtime instead, set {CudaRuntimeEnvironmentVariable} "
            + $"to the directory containing {runtimeFileName}.");
    }

    public static string? TryEnsureLoaded() => TryEnsureLoadedResult().LoadedPath;

    internal static IEnumerable<string> DiscoverSearchDirectories(string? pluginDirectory = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? directory in EnumerateConfiguredDirectories(pluginDirectory))
        {
            if (TryAddDirectory(seen, directory, out string? normalized))
            {
                yield return normalized!;
            }
        }
    }

    /// <summary>
    /// Reports installed CUDA toolkit major versions so a machine with only a mismatched CUDA major
    /// gets an honest message instead of an opaque loader error later in EP registration.
    /// </summary>
    internal static string DescribeInstalledCudaMajorVersions()
    {
        var majors = new SortedSet<int>();
        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string cudaRoot = Path.Join(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
            if (Directory.Exists(cudaRoot))
            {
                foreach (string versionDirectory in EnumerateChildDirectoriesSafe(cudaRoot))
                {
                    if (TryParseCudaMajor(Path.GetFileName(versionDirectory), out int major))
                    {
                        majors.Add(major);
                    }
                }
            }
        }

        string? cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrWhiteSpace(cudaPath) &&
            TryParseCudaMajor(Path.GetFileName(cudaPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), out int pathMajor))
        {
            majors.Add(pathMajor);
        }

        if (majors.Count == 0)
        {
            return "No CUDA Toolkit install was detected.";
        }

        var builder = new StringBuilder("Detected CUDA Toolkit major version(s): ");
        builder.Append(string.Join(", ", majors));
        builder.Append('.');
        if (!majors.Contains(RequiredCudaMajorVersion))
        {
            builder.Append($" None is CUDA {RequiredCudaMajorVersion}; the {TensorRtRtxProviderConstants.BundledCudaVariant} plugin cannot use them.");
        }

        return builder.ToString();
    }
    private static bool TryParseCudaMajor(string directoryName, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return false;
        }

        // Accept "v12.6", "12.6", "CUDA 12", "cuda-12"
        string token = directoryName.Trim();
        if (token.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            token = token[1..];
        }

        if (token.StartsWith("cuda", StringComparison.OrdinalIgnoreCase))
        {
            token = token["cuda".Length..].TrimStart('-', ' ');
        }

        int dot = token.IndexOf('.');
        string majorToken = dot >= 0 ? token[..dot] : token;
        return int.TryParse(majorToken, out major) && major > 0;
    }

    private static IEnumerable<string?> EnumerateConfiguredDirectories(string? pluginDirectory)
    {
        // The bundle ships its CUDA runtime beside the plugin; prefer it over any system install.
        yield return pluginDirectory;
        yield return Environment.GetEnvironmentVariable(CudaRuntimeEnvironmentVariable);

        string? cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrWhiteSpace(cudaPath))
        {
            yield return cudaPath;
            yield return Path.Join(cudaPath, "lib64");
            yield return Path.Join(cudaPath, "lib");
        }

        if (OperatingSystem.IsLinux())
        {
            yield return "/usr/local/cuda/lib64";
            yield return $"/usr/local/cuda-{RequiredCudaMajorVersion}/lib64";
            yield return "/usr/lib/x86_64-linux-gnu";
        }
    }
    /// <summary>
    /// Enumerates immediate child directories under a fixed local root.
    /// Swallows access / IO failures so CUDA discovery cannot crash bootstrap
    /// on locked folders, broken junctions, or unreadable install trees.
    /// </summary>
    internal static IEnumerable<string> EnumerateChildDirectoriesSafe(string root)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateDirectories(root).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = enumerator.MoveNext();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                if (!moved)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    private static bool TryAddDirectory(HashSet<string> seen, string? directory, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException
                or SecurityException)
        {
            return false;
        }

        try
        {
            return seen.Add(normalized) && Directory.Exists(normalized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void PrependProcessPath(string directory)
    {
        string? currentPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            Environment.SetEnvironmentVariable("PATH", directory);
            return;
        }

        if (currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => string.Equals(entry, directory, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + currentPath);
    }

    // The CUDA runtime is preloaded once per process so it stays resident for ORT's
    // subsequent TRT RTX plugin load; the module is intentionally never freed. Handles are
    // retained (keyed by resolved path) so repeated bootstrap attempts reuse the already
    // loaded module instead of leaking a fresh loader reference on every retry.
    // Path comparison must match the host filesystem's case semantics: Windows and macOS are
    // case-insensitive by default, Linux is case-sensitive. An OrdinalIgnoreCase key on Linux
    // would conflate two distinct runtime paths differing only by case and skip loading the
    // second one.
    private static readonly Dictionary<string, nint> LoadedNativeLibraries =
        new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    private static readonly object LoadedNativeLibrariesLock = new();

    private static bool TryLoadNativeLibrary(string libraryPath)
    {
        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(libraryPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            resolvedPath = libraryPath;
        }

        lock (LoadedNativeLibrariesLock)
        {
            if (LoadedNativeLibraries.ContainsKey(resolvedPath))
            {
                // Already loaded this process; reuse the retained handle, don't reload.
                return true;
            }

            try
            {
                if (NativeLibrary.TryLoad(resolvedPath, out nint handle))
                {
                    LoadedNativeLibraries[resolvedPath] = handle;
                    return true;
                }

                return false;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                // PATH mutation above is still useful for downstream native loads.
                return false;
            }
        }
    }
}
