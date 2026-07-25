namespace Trackdub.Inference.Runtime.ModelManifest;

/// <summary>
/// Documented primary inference runtimes for bundled models (manifest governance).
/// Pipeline selection still uses live provider discovery; this field is not a readiness claim.
/// </summary>
public static class ModelExpectedRuntime
{
    public const string OrtGenAi = "ort-genai";
    public const string OnnxCpu = "onnxruntime-cpu";
    public const string OnnxDnnl = "onnxruntime-dnnl";
    public const string OnnxDirectMl = "onnxruntime-directml";
    public const string OnnxMigraphx = "onnxruntime-migraphx";
    public const string OnnxDirectMlOrMigraphx = "onnxruntime-directml|onnxruntime-migraphx";
    public const string OnnxCudaOrMigraphx = "onnxruntime-cuda|onnxruntime-migraphx";

    /// <summary>Canonical Windows ONNX governance token (catalog-first narrative).</summary>
    public const string WindowsMlCatalogOrMigraphxOrDirectMl = "windows-ml|onnxruntime-migraphx|onnxruntime-directml";
}
