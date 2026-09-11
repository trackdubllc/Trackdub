using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.Runtime;

internal static class GenAiExecutionProviderNames
{
    // ONNX Runtime GenAI Config.AppendProvider accepts NvTensorRtRtx, not the
    // Trackdub pin token trt-rtx and not the ORT plugin registration name.
    public const string TensorRtRtx = "NvTensorRtRtx";

    public static string Resolve(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.DirectMl => "dml",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.TensorRTRtx => TensorRtRtx,
            ExecutionProviderKind.CoreMl => "coreml",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported GenAI execution provider.")
        };
}
