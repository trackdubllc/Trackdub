using Trackdub.Contracts;
using Trackdub.Application.Runtime;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Tests;

public sealed class HardwareOverrideCatalogTests
{
    [Fact]
    public void ProviderChoices_include_auto_cpu_and_gpu_routes()
    {
        HardwareOverrideProviderChoice[] choices = HardwareOverrideCatalog.ProviderChoices.ToArray();

        Assert.Contains(choices, choice => choice.Provider is null && choice.DisplayName == "Auto (planner + Windows ML catalog)");
        Assert.Contains(choices, choice => choice.Provider is ExecutionProviderKind.Cpu && choice.DisplayName == "CPU");
        Assert.Contains(choices, choice => choice.Provider is ExecutionProviderKind.DirectMl && choice.DisplayName == "DirectML (legacy GPU)");
        Assert.Contains(choices, choice => choice.Provider is ExecutionProviderKind.Migraphx && choice.DisplayName == "MIGraphX (AMD)");
        Assert.Contains(choices, choice => choice.Provider is ExecutionProviderKind.TensorRTRtx && choice.DisplayName == "TensorRT RTX");
        Assert.Contains(choices, choice => choice.Provider is ExecutionProviderKind.Cuda && choice.DisplayName == "CUDA");
        Assert.Contains(choices, choice => choice.Provider is ExecutionProviderKind.TensorRt && choice.DisplayName == "TensorRT");
    }

    [Fact]
    public void BuildDiscoveredProviderChoices_filters_to_loadable_providers()
    {
        ProviderCapability[] capabilities =
        [
            new() { Provider = ExecutionProviderKind.Cpu, ProviderLoadable = true },
            new() { Provider = ExecutionProviderKind.DirectMl, ProviderLoadable = true },
            new() { Provider = ExecutionProviderKind.TensorRTRtx, ProviderLoadable = false },
            new() { Provider = ExecutionProviderKind.Cuda, ProviderLoadable = false }
        ];

        HardwareOverrideProviderChoice[] choices =
            HardwareOverrideCatalog.BuildDiscoveredProviderChoices(capabilities, "Separation").ToArray();

        Assert.Contains(choices, choice => choice.Provider is null);
        Assert.Contains(choices, choice => choice.Provider is ExecutionProviderKind.Cpu);
        Assert.Contains(choices, choice => choice.Provider is ExecutionProviderKind.DirectMl);
        Assert.DoesNotContain(choices, choice => choice.Provider is ExecutionProviderKind.TensorRTRtx);
        Assert.DoesNotContain(choices, choice => choice.Provider is ExecutionProviderKind.Cuda);
    }

    [Fact]
    public void TryResolvePipelineHardwareOverrideKey_maps_transcribe_by_asr_engine()
    {
        Assert.True(
            HardwareOverrideCatalog.TryResolvePipelineHardwareOverrideKey(
                StageNames.Asr,
                AsrModelOverride.GenAi,
                out string genAiKey));
        Assert.Equal("AsrGenAi", genAiKey);

        Assert.True(
            HardwareOverrideCatalog.TryResolvePipelineHardwareOverrideKey(
                StageNames.Asr,
                AsrModelOverride.OnnxRuntime,
                out string onnxKey));
        Assert.Equal("AsrOnnxRuntime", onnxKey);

        Assert.True(
            HardwareOverrideCatalog.TryResolvePipelineHardwareOverrideKey(
                StageNames.Asr,
                AsrModelOverride.Nemotron35,
                out string nemotronKey));
        Assert.Equal("AsrNemotron", nemotronKey);
    }

    [Fact]
    public void TryResolvePipelineHardwareOverrideKey_maps_overlap_rescue_stage()
    {
        Assert.True(
            HardwareOverrideCatalog.TryResolvePipelineHardwareOverrideKey(
                StageNames.OverlapRescue,
                AsrModelOverride.Auto,
                out string overlapKey));
        Assert.Equal("OverlapRescue", overlapKey);

        Assert.True(HardwareOverrideCatalog.PipelineStageSupportsExecutionProviderSelection(StageNames.OverlapRescue));
        Assert.Contains(
            HardwareOverrideCatalog.StageChoices,
            choice => string.Equals(choice.StageKey, "OverlapRescue", StringComparison.Ordinal));
    }

    [Fact]
    public void TryResolvePipelineHardwareOverrideKey_maps_text_refinement_stage()
    {
        Assert.True(
            HardwareOverrideCatalog.TryResolvePipelineHardwareOverrideKey(
                StageNames.TextRefinementAsr,
                AsrModelOverride.Auto,
                out string textRefinementKey));
        Assert.Equal("TextRefinement", textRefinementKey);

        Assert.True(HardwareOverrideCatalog.PipelineStageSupportsExecutionProviderSelection(StageNames.TextRefinementAsr));
        Assert.Contains(
            HardwareOverrideCatalog.StageChoices,
            choice => string.Equals(choice.StageKey, "TextRefinement", StringComparison.Ordinal));
    }


    [Fact]
    public void CreateOverrides_omits_auto_choices_and_keeps_selected_providers()
    {
        IReadOnlyDictionary<string, ExecutionProviderKind> overrides = HardwareOverrideCatalog.CreateOverrides(
            [
                new HardwareOverrideSelection("Vad", null),
                new HardwareOverrideSelection("Translation", ExecutionProviderKind.DirectMl),
                new HardwareOverrideSelection("Tts", ExecutionProviderKind.Cpu)
            ]);

        Assert.Equal(2, overrides.Count);
        Assert.False(overrides.ContainsKey("Vad"));
        Assert.Equal(ExecutionProviderKind.DirectMl, overrides["Translation"]);
        Assert.Equal(ExecutionProviderKind.Cpu, overrides["Tts"]);
    }
}
