using System.Runtime.InteropServices;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>
/// Ensures CUDA 12 runtime libraries are visible before ORT loads the TensorRT RTX EP plugin.
/// </summary>
internal static class TensorRtRtxCudaRuntimeBootstrap
{
    private const string WindowsCudaRuntimeFileName = "cudart64_12.dll";
    private const string LinuxCudaRuntimeFileName = "libcudart.so.12";
    private const string CudaRuntimeEnvironmentVariable = TensorRtRtxProviderConstants.CudaRuntimeBinDirectoryEnvironmentVariable;

    public static string? TryEnsureLoaded()
    {
        string runtimeFileName = OperatingSystem.IsWindows()
            ? WindowsCudaRuntimeFileName
            : LinuxCudaRuntimeFileName;

        foreach (string directory in DiscoverSearchDirectories())
        {
            string candidatePath = Path.Combine(directory, runtimeFileName);
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            PrependProcessPath(directory);
            if (TryLoadNativeLibrary(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }

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

    private static IEnumerable<string?> EnumerateConfiguredDirectories()
    {
        yield return Environment.GetEnvironmentVariable(CudaRuntimeEnvironmentVariable);

        string? cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrWhiteSpace(cudaPath))
        {
            yield return cudaPath;
            yield return Path.Combine(cudaPath, "bin");
            yield return Path.Combine(cudaPath, "bin", "x64");
        }

        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string cudaRoot = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
            if (Directory.Exists(cudaRoot))
            {
                foreach (string versionDirectory in Directory.EnumerateDirectories(cudaRoot))
                {
                    yield return Path.Combine(versionDirectory, "bin");
                    yield return Path.Combine(versionDirectory, "bin", "x64");
                }
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string pythonRoot in new[]
                     {
                         Path.Combine(appData, "Python"),
                         Path.Combine(appData, "Local", "Programs", "Python"),
                     })
            {
                if (!Directory.Exists(pythonRoot))
                {
                    continue;
                }

                foreach (string pythonVersionDir in Directory.EnumerateDirectories(pythonRoot))
                {
                    // System/venv layout: Python\Python3X\Lib\site-packages
                    yield return Path.Combine(pythonVersionDir, "Lib", "site-packages", "nvidia", "cuda_runtime", "bin");
                    // pip --user layout: %APPDATA%\Python\Python3X\site-packages (no Lib prefix)
                    yield return Path.Combine(pythonVersionDir, "site-packages", "nvidia", "cuda_runtime", "bin");
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
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return seen.Add(normalized) && Directory.Exists(normalized);
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

    private static bool TryLoadNativeLibrary(string libraryPath)
    {
        try
        {
            if (NativeLibrary.TryLoad(libraryPath, out nint _))
            {
                return true;
            }

            NativeLibrary.Load(libraryPath);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            // PATH mutation above is still useful for downstream native loads.
            return false;
        }
    }
}
