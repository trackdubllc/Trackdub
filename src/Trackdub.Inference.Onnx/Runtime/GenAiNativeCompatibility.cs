namespace Trackdub.Inference.Onnx.Runtime;

internal static class GenAiNativeCompatibility
{
    public static void EnsureCompatible()
    {
        if (OperatingSystem.IsWindows())
            GenAiNativeRuntimeSelection.EnsureGenAiLoaded();
    }
}
