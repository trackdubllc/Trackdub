using Trackdub.Inference.Onnx.Runtime;

namespace Trackdub.Inference.Onnx.WindowsMl;

internal static class WindowsMlOnnxRuntimeNativeResolver
{
    public static void EnsureInitialized()
    {
        if (OperatingSystem.IsWindows())
            GenAiNativeRuntimeSelection.EnsureOrtLoaded();
    }
}
