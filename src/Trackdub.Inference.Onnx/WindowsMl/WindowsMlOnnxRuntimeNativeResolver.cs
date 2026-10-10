using System.Diagnostics;
using Trackdub.Inference.Onnx.Runtime;

namespace Trackdub.Inference.Onnx.WindowsMl;

internal static class WindowsMlOnnxRuntimeNativeResolver
{
    public static void EnsureInitialized()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

#if TRACKDUB_ORT_DNNL
        // The DNNL flavor ships its own native runtime. It is not a Windows ML or stock CUDA pair.
        GenAiNativeRuntimeSelection.EnsureOrtLoaded(requireVerified: false);
        Trace.TraceInformation("ORT native load skipped provenance verification for the DNNL runtime flavor.");
#else
        GenAiNativeRuntimeSelection.EnsureOrtLoaded(requireVerified: true);
        Trace.TraceInformation("ORT native provenance verified. " + GenAiNativeRuntimeSelection.Describe());
#endif
    }
}
