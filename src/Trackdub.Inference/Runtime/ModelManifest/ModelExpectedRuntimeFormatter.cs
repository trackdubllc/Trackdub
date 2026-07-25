namespace Trackdub.Inference.Runtime.ModelManifest;

/// <summary>
/// Maps manifest <c>expected_runtime</c> tokens to short Model Manager hint text.
/// Does not imply runtime readiness — live provider discovery still governs execution.
/// </summary>
public static class ModelExpectedRuntimeFormatter
{
    public static string? FormatHint(string? expectedRuntime)
    {
        if (string.IsNullOrWhiteSpace(expectedRuntime))
        {
            return null;
        }

        string normalized = expectedRuntime.Trim();
        if (normalized.Equals(ModelExpectedRuntime.OrtGenAi, StringComparison.OrdinalIgnoreCase))
        {
            return "Runtime: ONNX Runtime GenAI";
        }

        if (normalized.Equals(ModelExpectedRuntime.OnnxCpu, StringComparison.OrdinalIgnoreCase))
        {
            return "Runtime: CPU (ONNX Runtime)";
        }

        if (normalized.Equals(ModelExpectedRuntime.OnnxDnnl, StringComparison.OrdinalIgnoreCase))
        {
            return "Runtime: Intel oneDNN (CPU)";
        }

        if (normalized.Equals(ModelExpectedRuntime.OnnxDirectMl, StringComparison.OrdinalIgnoreCase))
        {
            return "Runtime: Windows GPU (DirectML fallback)";
        }

        if (normalized.Equals(ModelExpectedRuntime.OnnxMigraphx, StringComparison.OrdinalIgnoreCase))
        {
            return "Runtime: MIGraphX (AMD GPU)";
        }

        if (normalized.Equals(ModelExpectedRuntime.WindowsMlCatalogOrMigraphxOrDirectMl, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ModelExpectedRuntime.OnnxDirectMlOrMigraphx, StringComparison.OrdinalIgnoreCase))
        {
            return "Runtime: Windows ML catalog, MIGraphX, or DirectML fallback";
        }

        if (normalized.Equals(ModelExpectedRuntime.OnnxCudaOrMigraphx, StringComparison.OrdinalIgnoreCase))
        {
            return "Runtime: CUDA or MIGraphX (GPU)";
        }

        if (normalized.Contains('|', StringComparison.Ordinal))
        {
            string[] parts = normalized.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            string joined = string.Join(" or ", parts.Select(FormatSingleToken));
            return $"Runtime: {joined}";
        }

        return $"Runtime: {FormatSingleToken(normalized)}";
    }

    private static string FormatSingleToken(string token) =>
        token.ToLowerInvariant() switch
        {
            ModelExpectedRuntime.OrtGenAi => "ONNX Runtime GenAI",
            ModelExpectedRuntime.OnnxCpu => "CPU (ONNX Runtime)",
            ModelExpectedRuntime.OnnxDnnl => "Intel oneDNN (CPU)",
            ModelExpectedRuntime.OnnxDirectMl => "Windows GPU (DirectML fallback)",
            ModelExpectedRuntime.OnnxMigraphx => "MIGraphX (AMD GPU)",
            "windows-ml" => "Windows ML catalog",
            _ => token
        };
}
