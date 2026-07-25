using Trackdub.Domain;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// Formats execution-provider and model provenance for logs and UI.
/// </summary>
public static class PipelineRuntimeProvenanceFormatter
{
    public static string FormatProviderLabel(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "unknown";
        }

        return provider.Trim().ToLowerInvariant() switch
        {
            "dml" or "directml" => "DirectML",
            "cpu" => "CPU",
            "tensorrt-rtx" or "tensorrtrtx" => "TensorRT RTX",
            "cuda" => "CUDA",
            "tensorrt" => "TensorRT",
            "coreml" => "CoreML",
            "openvino" or "openvino-catalog" => "OpenVINO",
            "migraphx" => "MIGraphX",
            "cloud" => "Cloud",
            _ => provider.Trim()
        };
    }

    public static string FormatModelLabel(string? modelAlias, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelAlias))
        {
            return modelAlias.Trim();
        }

        return string.IsNullOrWhiteSpace(modelId) ? "unknown" : modelId.Trim();
    }

    public static string FormatVariantLabel(string? variant) =>
        string.IsNullOrWhiteSpace(variant) ? string.Empty : variant.Trim();

    public static string FormatCompact(params string?[] parts)
    {
        IEnumerable<string> normalized = parts
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .Select(static part => part!.Trim());
        return string.Join(" · ", normalized);
    }

    public static string FormatStageRunLine(StageRunRuntimeInfo? runtime)
    {
        if (runtime is null)
        {
            return string.Empty;
        }

        return FormatCompact(
            FormatProviderLabel(runtime.SelectedProvider),
            FormatVariantLabel(runtime.ModelVariant),
            FormatModelLabel(runtime.ModelAlias, runtime.ModelId));
    }

    public static string FormatTtsSegmentLogLine(
        int segmentIndex,
        string? provider,
        string? modelAlias,
        string? modelId,
        string? variant,
        string? voiceId = null)
    {
        string model = FormatModelLabel(modelAlias, modelId);
        string variantLabel = FormatVariantLabel(variant);
        string providerLabel = FormatProviderLabel(provider);
        string voice = string.IsNullOrWhiteSpace(voiceId) ? string.Empty : $" voice={voiceId.Trim()}";
        string variantSuffix = string.IsNullOrWhiteSpace(variantLabel) ? string.Empty : $" variant={variantLabel}";
        return $"TTS segment {segmentIndex}: provider={providerLabel} model={model}{variantSuffix}{voice}";
    }

    public static string FormatStageSegmentLogLine(
        string stageName,
        int segmentIndex,
        StageRunRuntimeInfo? runtime)
    {
        if (runtime is null)
        {
            return $"{stageName} segment {segmentIndex}: runtime metadata not recorded.";
        }

        return $"{stageName} segment {segmentIndex}: provider={FormatProviderLabel(runtime.SelectedProvider)} model={FormatModelLabel(runtime.ModelAlias, runtime.ModelId)} variant={FormatVariantLabel(runtime.ModelVariant)}";
    }

    public static string FormatCollapsedSegmentBadge(
        string? dubProvider,
        string? dubVariant,
        string? translationProvider,
        string? asrProvider)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(dubProvider))
        {
            string variant = FormatVariantLabel(dubVariant);
            parts.Add(string.IsNullOrWhiteSpace(variant)
                ? $"dub {dubProvider}"
                : $"dub {dubProvider}·{variant}");
        }

        if (!string.IsNullOrWhiteSpace(translationProvider))
        {
            parts.Add($"tr {translationProvider}");
        }

        if (!string.IsNullOrWhiteSpace(asrProvider))
        {
            parts.Add($"asr {asrProvider}");
        }

        return string.Join(" · ", parts);
    }

    public static string FormatActiveSegmentDiagnosticsLine(
        int segmentIndex,
        string? dubDetail,
        string? translationDetail,
        string? asrDetail,
        string? alignDetail = null)
    {
        List<string> parts = [$"seg {segmentIndex}"];
        if (!string.IsNullOrWhiteSpace(dubDetail))
        {
            parts.Add($"dub: {dubDetail}");
        }

        if (!string.IsNullOrWhiteSpace(translationDetail))
        {
            parts.Add($"tr: {translationDetail}");
        }

        if (!string.IsNullOrWhiteSpace(asrDetail))
        {
            parts.Add($"asr: {asrDetail}");
        }

        if (!string.IsNullOrWhiteSpace(alignDetail))
        {
            parts.Add($"align: {alignDetail}");
        }

        if (parts.Count == 1)
        {
            parts.Add("no pipeline provenance recorded");
        }

        return string.Join(" · ", parts);
    }

    public static string? FormatRuntimeDetail(StageRunRuntimeInfo? runtime) =>
        runtime is null
            ? null
            : FormatCompact(
                FormatProviderLabel(runtime.SelectedProvider),
                FormatVariantLabel(runtime.ModelVariant),
                FormatModelLabel(runtime.ModelAlias, runtime.ModelId));
}
