using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubDubbingEngineSnapshotTests
{
    [Fact]
    public void MergeRuntimeModelSelectionsIntoSnapshot_adds_settings_derived_pack_aliases()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TargetLanguageCode"] = "de",
        };

        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            AsrModelAlias: "whisper-tiny-onnx",
            TranslationModelAlias: "phi-4-mini",
            TtsModelAlias: "kokoro-onnx");

        TrackdubDubbingEngine.MergeRuntimeModelSelectionsIntoSnapshot(snapshot, selections);

        Assert.Equal("whisper-tiny-onnx", snapshot[$"Model:{StageNames.Asr}"]);
        Assert.Equal("phi-4-mini", snapshot[$"Model:{StageNames.Translation}"]);
        Assert.Equal("kokoro-onnx", snapshot[$"Model:{StageNames.Tts}"]);
        Assert.False(snapshot.ContainsKey($"Model:{StageNames.TextRefinementAsr}"));
    }

    [Fact]
    public void MergeRuntimeModelSelectionsIntoSnapshot_includes_text_refinement_when_enabled()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TextRefinementModelAlias: "qwen2.5-0.5b-instruct-genai",
            EnableAsrTextRefinement: true);

        TrackdubDubbingEngine.MergeRuntimeModelSelectionsIntoSnapshot(snapshot, selections);

        Assert.Equal("qwen2.5-0.5b-instruct-genai", snapshot[$"Model:{StageNames.TextRefinementAsr}"]);
    }

    [Fact]
    public void MergeRuntimeModelSelectionsIntoSnapshot_overwrites_stale_per_run_preferences()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"Model:{StageNames.Asr}"] = "stale-session-alias",
        };

        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            AsrModelAlias: "whisper-tiny-onnx");

        TrackdubDubbingEngine.MergeRuntimeModelSelectionsIntoSnapshot(snapshot, selections);

        Assert.Equal("whisper-tiny-onnx", snapshot[$"Model:{StageNames.Asr}"]);
    }

    [Fact]
    public void MergeRuntimeModelSelectionsIntoSnapshot_adds_resolved_model_variants()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TranslationModelAlias: "phi-4-mini",
            ModelVariantOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelVariantOverrideKeys.Build(StageNames.Translation, "phi-4-mini")] = "gpu-int4",
            });

        TrackdubDubbingEngine.MergeRuntimeModelSelectionsIntoSnapshot(snapshot, selections);

        Assert.Equal("phi-4-mini", snapshot[$"Model:{StageNames.Translation}"]);
        Assert.Equal("gpu-int4", snapshot[$"ModelVariant:{StageNames.Translation}"]);
    }

    [Fact]
    public void MergeRuntimeModelSelectionsIntoSnapshot_includes_lip_stage_aliases()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            LipSyncModelAlias: "wav2vec2-lv60-espeak-cv-ft-onnx",
            LipSynthesisModelAlias: "ByteDance/LatentSync-1.6");

        TrackdubDubbingEngine.MergeRuntimeModelSelectionsIntoSnapshot(snapshot, selections);

        Assert.Equal("wav2vec2-lv60-espeak-cv-ft-onnx", snapshot[$"Model:{StageNames.LipSync}"]);
        Assert.Equal("ByteDance/LatentSync-1.6", snapshot[$"Model:{StageNames.LipSynthesis}"]);
    }
}
