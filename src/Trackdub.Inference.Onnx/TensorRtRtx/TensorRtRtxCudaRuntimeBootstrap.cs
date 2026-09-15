using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>Result of probing/loading the CUDA 12 runtime required by the TRT RTX EP ABI plugin (cu12).</summary>
internal sealed record TensorRtRtxCudaRuntimeEnsureResult(
    bool Succeeded,
    string? LoadedPath,
    string Detail)
{
    public static TensorRtRtxCudaRuntimeEnsureResult Failed(string detail) =>
        new(false, null, detail);
}

/// <summary>
/// Ensures CUDA 12 runtime libraries are visible before ORT loads the TensorRT RTX EP plugin.
/// The shipping TRT RTX EP ABI bundle is cu12; a machine with only CUDA 13.x is not ready.
/// </summary>
internal static class TensorRtRtxCudaRuntimeBootstrap
{
    private const string WindowsCudaRuntimeFileName = "cudart64_12.dll";
    private const string LinuxCudaRuntimeFileName = "libcudart.so.12";
    private const string CudaRuntimeEnvironmentVariable = TensorRtRtxProviderConstants.CudaRuntimeBinDirectoryEnvironmentVariable;

    public static TensorRtRtxCudaRuntimeEnsureResult TryEnsureLoadedResult() =>
        TryEnsureLoadedResult(DiscoverSearchDirectories());

    internal static TensorRtRtxCudaRuntimeEnsureResult TryEnsureLoadedResult(IEnumerable<string> searchDirectories)
    {
        string runtimeFileName = OperatingSystem.IsWindows()
            ? WindowsCudaRuntimeFileName
            : LinuxCudaRuntimeFileName;

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
                    $"CUDA 12 runtime loaded from '{candidatePath}'.");
            }

            return TensorRtRtxCudaRuntimeEnsureResult.Failed(
                $"CUDA 12 runtime file exists at '{candidatePath}' but failed to load "
                + $"(Win32 error may be 126). Inspect dependencies or reinstall CUDA 12 runtime.");
        }

        string installedCudaHint = DescribeInstalledCudaMajorVersions();
        return TensorRtRtxCudaRuntimeEnsureResult.Failed(
            "CUDA 12 runtime (" + runtimeFileName + ") was not found in configured search paths. "
            + "The TensorRT RTX EP ABI bundle is cu12 and will not use a CUDA 13 runtime. "
            + installedCudaHint
            + " Install CUDA Toolkit 12.x, run `pip install nvidia-cuda-runtime-cu12`, "
            + $"or set {CudaRuntimeEnvironmentVariable} to the directory containing {runtimeFileName}.");
    }

    public static string? TryEnsureLoaded() => TryEnsureLoadedResult().LoadedPath;

    internal static IEnumerable<string> DiscoverSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? directory in EnumerateConfiguredDirectories())
        {
            if (TryAddDirectory(seen, directory, out string? normalized))
            {
                yield return normalized!;
            }
        }
    }

    /// <summary>
    /// Reports installed CUDA toolkit major versions so a CUDA 13-only machine gets an honest
    /// "found 13, need 12" message instead of an opaque Error 126 later in EP registration.
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
        if (majors.Contains(12))
        {
            builder.Append(" CUDA 12 is installed; ensure its bin directory is on PATH or TRACKDUB_CUDA12_BIN_DIR.");
        }
        else if (majors.Count == 1 && majors.Contains(13))
        {
            builder.Append(" Only CUDA 13 is installed; TRT RTX cu12 requires the CUDA 12 runtime DLL.");
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

    private static IEnumerable<string?> EnumerateConfiguredDirectories()
    {
        yield return Environment.GetEnvironmentVariable(CudaRuntimeEnvironmentVariable);

        string? cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrWhiteSpace(cudaPath))
        {
            yield return cudaPath;
            yield return Path.Join(cudaPath, "bin");
            yield return Path.Join(cudaPath, "bin", "x64");
            yield return Path.Join(cudaPath, "lib64");
            yield return Path.Join(cudaPath, "lib");
        }

        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string cudaRoot = Path.Join(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
            if (Directory.Exists(cudaRoot))
            {
                // Prefer CUDA 12 installs so a machine with both 12 and 13 loads cu12 first.
                foreach (string versionDirectory in EnumerateChildDirectoriesSafe(cudaRoot)
                             .OrderBy(static path => PreferCuda12First(path)))
                {
                    yield return Path.Join(versionDirectory, "bin");
                    yield return Path.Join(versionDirectory, "bin", "x64");
                }
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string pythonRoot in new[]
                     {
                         Path.Join(appData, "Python"),
                         Path.Join(appData, "Local", "Programs", "Python"),
                     })
            {
                if (!Directory.Exists(pythonRoot))
                {
                    continue;
                }

                foreach (string pythonVersionDir in EnumerateChildDirectoriesSafe(pythonRoot))
                {
                    // System/venv layout: Python\Python3X\Lib\site-packages
                    yield return Path.Join(pythonVersionDir, "Lib", "site-packages", "nvidia", "cuda_runtime", "bin");
                    // pip --user layout: %APPDATA%\Python\Python3X\site-packages (no Lib prefix)
                    yield return Path.Join(pythonVersionDir, "site-packages", "nvidia", "cuda_runtime", "bin");
                }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/local/cuda/lib64";
            yield return "/usr/local/cuda-12/lib64";
            yield return "/usr/lib/x86_64-linux-gnu";
        }
    }

    private static int PreferCuda12First(string path)
    {
        if (TryParseCudaMajor(Path.GetFileName(path), out int major))
        {
            return major == 12 ? 0 : major;
        }

        return 100;
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

    // The CUDA 12 runtime is preloaded once per process so it stays resident for ORT's
    // subsequent TRT RTX plugin load; the module is intentionally never freed. Handles are
    // retained (keyed by resolved path) so repeated bootstrap attempts reuse the already
    // loaded module instead of leaking a fresh loader reference on every retry.
    private static readonly Dictionary<string, nint> LoadedNativeLibraries =
        new(StringComparer.OrdinalIgnoreCase);
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
