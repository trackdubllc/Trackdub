using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Runtime;

public sealed record HardwareOverrideProviderChoice(
    ExecutionProviderKind? Provider,
    string DisplayName);

public sealed record HardwareOverrideStageChoice(
    string StageKey,
    string DisplayName);

public sealed record HardwareOverrideSelection(
    string StageKey,
    ExecutionProviderKind? Provider);

public static class HardwareOverrideCatalog
{
    public static IReadOnlyDictionary<string, ExecutionProviderKind> EmptyOverrides { get; } =
        new Dictionary<string, ExecutionProviderKind>();

    public static IReadOnlyList<HardwareOverrideProviderChoice> ProviderChoices { get; } =
    [
        new(null, "Auto (planner + Windows ML catalog)"),
        new(ExecutionProviderKind.Cpu, "CPU"),
        new(ExecutionProviderKind.Dnnl, "Intel oneDNN (CPU)"),
        new(ExecutionProviderKind.DirectMl, "DirectML (legacy GPU)"),
        new(ExecutionProviderKind.Migraphx, "MIGraphX (AMD)"),
        new(ExecutionProviderKind.OpenVinoCatalog, "OpenVINO (Windows ML catalog)"),
        new(ExecutionProviderKind.Qnn, "QNN (Windows ML catalog)"),
        new(ExecutionProviderKind.VitisAi, "VitisAI (Windows ML catalog)"),
        new(ExecutionProviderKind.TensorRTRtx, "TensorRT RTX"),
        new(ExecutionProviderKind.Cuda, "CUDA"),
        new(ExecutionProviderKind.TensorRt, "TensorRT")
    ];

    public static IReadOnlyList<HardwareOverrideStageChoice> StageChoices { get; } =
    [
        new("Vad", "Voice Activity Detection"),
        new("AsrGenAi", "Transcription (GenAI)"),
        new("AsrOnnxRuntime", "Transcription (ONNX)"),
        new("AsrNemotron", "Transcription (Nemotron 3.5)"),
        new("Separation", "Stem Separation"),
        new("OverlapRescue", "Overlap Speech Rescue"),
        new("Diarization", "Speaker Diarization"),
        new("TextRefinement", "ASR Text Polish"),
        new("Translation", "Text Translation"),
        new("Tts", "Text-to-Speech")
    ];

    public static IReadOnlyList<HardwareOverrideProviderChoice> GetProviderChoicesForStage(string stageKey)
    {
        if (string.Equals(stageKey, "AsrGenAi", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderChoices.Where(static c => c.Provider != ExecutionProviderKind.TensorRTRtx).ToArray();
        }

        return ProviderChoices;
    }

    public static bool PipelineStageSupportsExecutionProviderSelection(string pipelineStageKey) =>
        string.Equals(pipelineStageKey, StageNames.Vad, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pipelineStageKey, StageNames.Separation, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pipelineStageKey, StageNames.Asr, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pipelineStageKey, StageNames.Diarization, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pipelineStageKey, StageNames.OverlapRescue, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pipelineStageKey, StageNames.Translation, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pipelineStageKey, StageNames.Tts, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pipelineStageKey, StageNames.TextRefinementAsr, StringComparison.OrdinalIgnoreCase);

    public static bool TryResolvePipelineHardwareOverrideKey(
        string pipelineStageKey,
        AsrModelOverride asrModelOverride,
        out string hardwareKey)
    {
        if (string.Equals(pipelineStageKey, StageNames.Separation, StringComparison.OrdinalIgnoreCase))
        {
            hardwareKey = "Separation";
            return true;
        }

        if (string.Equals(pipelineStageKey, StageNames.Asr, StringComparison.OrdinalIgnoreCase))
        {
            hardwareKey = asrModelOverride switch
            {
                AsrModelOverride.GenAi => "AsrGenAi",
                AsrModelOverride.OnnxRuntime => "AsrOnnxRuntime",
                AsrModelOverride.Nemotron35 => "AsrNemotron",
                _ => "Asr"
            };
            return true;
        }

        if (string.Equals(pipelineStageKey, StageNames.Diarization, StringComparison.OrdinalIgnoreCase))
        {
            hardwareKey = "Diarization";
            return true;
        }

        if (string.Equals(pipelineStageKey, StageNames.OverlapRescue, StringComparison.OrdinalIgnoreCase))
        {
            hardwareKey = "OverlapRescue";
            return true;
        }

        if (string.Equals(pipelineStageKey, StageNames.Translation, StringComparison.OrdinalIgnoreCase))
        {
            hardwareKey = "Translation";
            return true;
        }

        if (string.Equals(pipelineStageKey, StageNames.Tts, StringComparison.OrdinalIgnoreCase))
        {
            hardwareKey = "Tts";
            return true;
        }

        if (string.Equals(pipelineStageKey, StageNames.TextRefinementAsr, StringComparison.OrdinalIgnoreCase))
        {
            hardwareKey = "TextRefinement";
            return true;
        }

        hardwareKey = string.Empty;
        return false;
    }

    public static IReadOnlyList<HardwareOverrideProviderChoice> BuildDiscoveredProviderChoices(
        IReadOnlyList<ProviderCapability> capabilities,
        string hardwareStageKey)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        HashSet<ExecutionProviderKind> loadable = capabilities
            .Where(static capability => capability.ProviderLoadable)
            .Select(static capability => capability.Provider)
            .ToHashSet();

        return GetProviderChoicesForStage(hardwareStageKey)
            .Where(choice => choice.Provider is null || loadable.Contains(choice.Provider.Value))
            .ToArray();
    }

    public static IReadOnlyDictionary<string, ExecutionProviderKind> CreateOverrides(
        IEnumerable<HardwareOverrideSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);

        return selections
            .Where(selection => !string.IsNullOrWhiteSpace(selection.StageKey) && selection.Provider is not null)
            .ToDictionary(
                selection => selection.StageKey,
                selection => selection.Provider!.Value,
                StringComparer.Ordinal);
    }
}
