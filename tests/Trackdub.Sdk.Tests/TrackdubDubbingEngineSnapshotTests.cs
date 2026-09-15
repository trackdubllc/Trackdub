using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubDubbingEngineSnapshotTests
{
    private static DubbingSessionOptions MinimalOptions() => new()
    {
        SourceMediaPath = "/media/source.mp4",
        TargetLanguageCode = "de",
    };

    [Fact]
    public void CaptureExecutionSnapshot_records_audio_flag_defaults()
    {
        Dictionary<string, string> snapshot =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(MinimalOptions());

        Assert.Equal("True", snapshot["ApplyTimbrePolish"]);
        Assert.Equal("False", snapshot["RestoreOriginalPan"]);
        Assert.Equal("False", snapshot["MatchOriginalLoudness"]);
    }

    [Fact]
    public void CaptureExecutionSnapshot_flipping_ApplyTimbrePolish_changes_value()
    {
        Dictionary<string, string> baseline =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(MinimalOptions());
        Dictionary<string, string> flipped = TrackdubDubbingEngine.CaptureExecutionSnapshot(
            MinimalOptions() with { ApplyTimbrePolish = false });

        Assert.NotEqual(baseline["ApplyTimbrePolish"], flipped["ApplyTimbrePolish"]);
        Assert.Equal("False", flipped["ApplyTimbrePolish"]);
    }

    [Fact]
    public void CaptureExecutionSnapshot_flipping_RestoreOriginalPan_changes_value()
    {
        Dictionary<string, string> baseline =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(MinimalOptions());
        Dictionary<string, string> flipped = TrackdubDubbingEngine.CaptureExecutionSnapshot(
            MinimalOptions() with { RestoreOriginalPan = true });

        Assert.NotEqual(baseline["RestoreOriginalPan"], flipped["RestoreOriginalPan"]);
        Assert.Equal("True", flipped["RestoreOriginalPan"]);
    }

    [Fact]
    public void CaptureExecutionSnapshot_flipping_MatchOriginalLoudness_changes_value()
    {
        Dictionary<string, string> baseline =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(MinimalOptions());
        Dictionary<string, string> flipped = TrackdubDubbingEngine.CaptureExecutionSnapshot(
            MinimalOptions() with { MatchOriginalLoudness = true });

        Assert.NotEqual(baseline["MatchOriginalLoudness"], flipped["MatchOriginalLoudness"]);
        Assert.Equal("True", flipped["MatchOriginalLoudness"]);
    }

    [Fact]
    public void CaptureExecutionSnapshot_flipping_BurnInSubtitles_changes_value()
    {
        Dictionary<string, string> baseline =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(MinimalOptions());
        Dictionary<string, string> flipped = TrackdubDubbingEngine.CaptureExecutionSnapshot(
            MinimalOptions() with { BurnInSubtitles = true });

        Assert.Equal("False", baseline["BurnInSubtitles"]);
        Assert.NotEqual(baseline["BurnInSubtitles"], flipped["BurnInSubtitles"]);
        Assert.Equal("True", flipped["BurnInSubtitles"]);
    }

    [Fact]
    public void CaptureExecutionSnapshot_maps_VideoEncoder_to_canonical_key()
    {
        Dictionary<string, string> nvenc = TrackdubDubbingEngine.CaptureExecutionSnapshot(
            MinimalOptions() with { VideoEncoder = VideoEncoderPreference.Nvenc });
        Dictionary<string, string> auto = TrackdubDubbingEngine.CaptureExecutionSnapshot(
            MinimalOptions() with { VideoEncoder = VideoEncoderPreference.Auto });

        Assert.Equal("nvenc", nvenc["VideoEncoder"]);
        Assert.Equal("auto", auto["VideoEncoder"]);
    }

    [Fact]
    public void CaptureExecutionSnapshot_SubtitleSource_null_and_translated_are_equivalent()
    {
        Dictionary<string, string> nullSource =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(
                MinimalOptions() with { SubtitleSource = null });
        Dictionary<string, string> translated =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(
                MinimalOptions() with { SubtitleSource = "translated" });
        Dictionary<string, string> transcript =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(
                MinimalOptions() with { SubtitleSource = "transcript" });

        Assert.Equal(nullSource["SubtitleSource"], translated["SubtitleSource"]);
        Assert.NotEqual(nullSource["SubtitleSource"], transcript["SubtitleSource"]);
    }

    [Fact]
    public void CaptureExecutionSnapshot_SubtitleFormats_null_empty_and_srt_are_distinct()
    {
        Dictionary<string, string> nullFormats =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(
                MinimalOptions() with { SubtitleFormats = null });
        Dictionary<string, string> emptyFormats =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(
                MinimalOptions() with { SubtitleFormats = new List<string>() });
        Dictionary<string, string> srtFormats =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(
                MinimalOptions() with { SubtitleFormats = new List<string> { "srt" } });

        Assert.Equal(3, new HashSet<string>(new[]
        {
            nullFormats["SubtitleFormats"],
            emptyFormats["SubtitleFormats"],
            srtFormats["SubtitleFormats"],
        }).Count);
    }

    [Fact]
    public void CaptureExecutionSnapshot_SubtitleFormats_are_case_insensitive()
    {
        Dictionary<string, string> lower =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(
                MinimalOptions() with { SubtitleFormats = new List<string> { "srt" } });
        Dictionary<string, string> upper =
            TrackdubDubbingEngine.CaptureExecutionSnapshot(
                MinimalOptions() with { SubtitleFormats = new List<string> { "SRT" } });

        Assert.Equal(lower["SubtitleFormats"], upper["SubtitleFormats"]);
    }

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
