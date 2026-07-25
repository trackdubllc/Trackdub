namespace Trackdub.Inference.Onnx.Runtime.Planning;

internal static class NativeTensorRtLibraryProbe
{
    public static bool IsNativeTensorRtAvailable()
    {
        if (OperatingSystem.IsLinux())
        {
            return EnumerateLinuxSearchDirectories().Any(ContainsNativeTensorRtLibrary);
        }

        if (OperatingSystem.IsWindows())
        {
            return EnumerateWindowsSearchDirectories().Any(ContainsNativeTensorRtLibrary);
        }

        return false;
    }

    private static bool ContainsNativeTensorRtLibrary(string directory)
    {
        if (!DirectoryExists(directory))
        {
            return false;
        }

        string pattern = OperatingSystem.IsWindows() ? "nvinfer.dll" : "libnvinfer.so*";
        return EnumerateFiles(directory, pattern).Any();
    }

    private static bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateLinuxSearchDirectories()
    {
        yield return "/usr/lib/x86_64-linux-gnu";
        yield return "/usr/lib/aarch64-linux-gnu";
        yield return "/usr/local/cuda/lib64";
        yield return "/usr/lib";

        string? ldLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (string.IsNullOrWhiteSpace(ldLibraryPath))
        {
            yield break;
        }

        foreach (string path in ldLibraryPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> EnumerateWindowsSearchDirectories()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string path in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
                }
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA", "v12.9", "bin");
        yield return Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA", "v12.8", "bin");
        yield return Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA", "v12.7", "bin");
        yield return Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA", "v12.6", "bin");
        yield return Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA", "v12.5", "bin");
        yield return Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA", "v12.4", "bin");
        yield return Path.Combine(programFiles, "NVIDIA Corporation", "TensorRT", "lib");
    }
}
