using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.Runtime;

internal static class GenAiExecutionProviderNames
{
    // ONNX Runtime GenAI Config.AppendProvider accepts NvTensorRtRtx, not the
    // Trackdub pin token trt-rtx and not the ORT plugin registration name.
    // ORT GenAI normalizes provider names via NormalizeProviderName (src/config.cpp),
    // which lowercases the input, strips the "ExecutionProvider" suffix, and maps the
    // result to a canonical dispatch-table name. The canonical names / accepted aliases
    // are: CPU, cuda, QNN, WebGPU, DML, OpenVINO, VitisAI, RyzenAI, NvTensorRtRtx, AMDGPU.
    public const string TensorRtRtx = "NvTensorRtRtx";

    // ORT GenAI exposes AMD GPU acceleration (MIGraphX / ROCm) through its AMDGPU device;
    // there is no separate "migraphx" provider name.
    public const string AmdGpu = "AMDGPU";

    // ORT GenAI has a single OpenVINO device shared by the standalone and Windows ML
    // catalog OpenVINO execution providers.
    public const string OpenVino = "OpenVINO";

    // ORT GenAI QNN device (Qualcomm QNN).
    public const string Qnn = "QNN";

    public static string Resolve(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.DirectMl => "dml",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.TensorRTRtx => TensorRtRtx,
            // Native Linux TensorRT is promoted to the TensorRT RTX device in ORT GenAI,
            // which only exposes NvTensorRtRtx for TensorRT-backed acceleration.
            ExecutionProviderKind.TensorRt => TensorRtRtx,
            // AMD MIGraphX / ROCm map to the ORT GenAI AMDGPU device.
            ExecutionProviderKind.Migraphx => AmdGpu,
            // Standalone OpenVINO and the Windows ML catalog OpenVINO EP both target the
            // single ORT GenAI OpenVINO device.
            ExecutionProviderKind.OpenVino => OpenVino,
            ExecutionProviderKind.OpenVinoCatalog => OpenVino,
            ExecutionProviderKind.Qnn => Qnn,
            // Intel oneDNN / DNNL is a CPU-side execution provider; ORT GenAI runs it on
            // its CPU device, so it resolves to the CPU provider name.
            ExecutionProviderKind.Dnnl => "cpu",
            ExecutionProviderKind.CoreMl => "coreml",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported GenAI execution provider.")
        };
}
