using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Runtime;

internal static class GenAiNativeCompatibility
{
    // Windows ML can place an older onnxruntime.dll beside the app while NuGet
    // supplies a newer managed ORT and GenAI pair. GenAI's Config finalizer calls
    // into its native DLL even after construction fails, which can terminate the host.
    public static void EnsureCompatible()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string nativePath = Path.Join(AppContext.BaseDirectory, "onnxruntime.dll");
        if (!File.Exists(nativePath) || IsWindowsMlRuntime(AppContext.BaseDirectory))
        {
            return;
        }

        Version? managedVersion = typeof(OrtEnv).Assembly.GetName().Version;
        FileVersionInfo nativeVersion = FileVersionInfo.GetVersionInfo(nativePath);
        if (managedVersion is null || IsCompatible(
            managedVersion, nativeVersion.FileMajorPart, nativeVersion.FileMinorPart))
        {
            return;
        }

        throw new InvalidOperationException(
            $"ONNX Runtime GenAI cannot load safely: the app loads ONNX Runtime " +
            $"{nativeVersion.FileMajorPart}.{nativeVersion.FileMinorPart} from '{nativePath}', " +
            $"but the managed runtime requires {managedVersion.Major}.{managedVersion.Minor}. " +
            "Install a matching native runtime before using a GenAI model.");
    }

    internal static bool IsCompatible(Version managedVersion, int nativeMajor, int nativeMinor) =>
        nativeMajor == managedVersion.Major && nativeMinor == managedVersion.Minor;

    // The Windows build ships GenAI's .WinML package, which is built against the Windows ML
    // runtime it sits beside rather than against the managed ORT package's version.
    internal static bool IsWindowsMlRuntime(string directory) =>
        File.Exists(Path.Join(directory, "Microsoft.Windows.AI.MachineLearning.dll"));
}
