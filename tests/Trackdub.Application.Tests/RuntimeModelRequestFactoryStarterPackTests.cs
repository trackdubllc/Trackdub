using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Tests;

public sealed class RuntimeModelRequestFactoryStarterPackTests
{
    [Fact]
    public void CreateSelectionsFromSettings_prefers_explicit_preferences_over_stage_aliases()
    {
        StudioSettings settings = StudioSettings.Default with
        {
            AsrModelOverride = AsrModelOverride.OnnxRuntime,
            StageModelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [StageNames.Asr] = "whisper-tiny-onnx",
                [StageNames.Translation] = "phi-4-mini",
            },
        };

        var explicitPreferences = new InferenceModelPreferences(AsrModelAlias: "session-whisper");

        RuntimeModelSelections selections = RuntimeModelRequestFactory.CreateSelectionsFromSettings(
            settings,
            explicitPreferences);

        Assert.Equal("session-whisper", selections.AsrModelAlias);
        Assert.Equal("phi-4-mini", selections.TranslationModelAlias);
    }

    [Fact]
    public void CreateSelectionsFromSettings_uses_stage_aliases_before_override_enums()
    {
        StudioSettings settings = StudioSettings.Default with
        {
            AsrModelOverride = AsrModelOverride.GenAi,
            TranslationModelOverride = TranslationModelOverride.Madlad,
            TtsModelOverride = TtsModelOverride.Kokoro,
            StageModelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [StageNames.Asr] = "whisper-tiny-onnx",
                [StageNames.Translation] = "phi-4-mini",
                [StageNames.Tts] = "kokoro-onnx",
            },
        };

        RuntimeModelSelections selections = RuntimeModelRequestFactory.CreateSelectionsFromSettings(settings);

        Assert.Equal("whisper-tiny-onnx", selections.AsrModelAlias);
        Assert.Equal("phi-4-mini", selections.TranslationModelAlias);
        Assert.Equal("kokoro-onnx", selections.TtsModelAlias);
    }

    [Fact]
    public void CreateSelectionsFromSettings_does_not_fall_back_to_override_enums_when_starter_pack_applied_without_alias()
    {
        StudioSettings settings = StudioSettings.Default with
        {
            AppliedStarterPackId = "balanced",
            AsrModelOverride = AsrModelOverride.GenAi,
            TranslationModelOverride = TranslationModelOverride.Madlad,
            StageModelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [StageNames.Translation] = "phi-4-mini",
            },
        };

        RuntimeModelSelections selections = RuntimeModelRequestFactory.CreateSelectionsFromSettings(settings);

        Assert.Null(selections.AsrModelAlias);
        Assert.Equal("phi-4-mini", selections.TranslationModelAlias);
    }

    [Fact]
    public void CreateModelPreferences_resolves_pack_scoped_variant_for_vad_without_explicit_alias()
    {
        StudioSettings settings = StudioSettings.Default with
        {
            AppliedStarterPackId = "basic",
            StageModelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [StageNames.Vad] = "silero-vad",
            },
            ModelVariantOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelVariantOverrideKeys.Build(StageNames.Vad, "silero-vad")] = "fp16",
                [StageNames.Vad] = "fp16",
            },
        };

        RuntimeModelSelections selections = RuntimeModelRequestFactory.CreateSelectionsFromSettings(settings);
        InferenceModelPreferences preferences = RuntimeModelRequestFactory.CreateModelPreferences(selections);

        Assert.Equal("fp16", preferences.GetPreferredModelVariantAlias(RuntimeStage.Vad));
    }

    [Fact]
    public void CreateStageRequest_resolves_translation_variant_from_composite_pack_key()
    {
        var options = new RuntimeModelRequestOptions(
            AsrModelOverride: AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TranslationModelAlias: "phi-4-mini",
            ModelVariantOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelVariantOverrideKeys.Build(StageNames.Translation, "phi-4-mini")] = "gpu-int4",
            });

        RuntimeModelRequest request = RuntimeModelRequestFactory.CreateTranslationRequest(options, "en", "de");

        Assert.Equal("gpu-int4", request.PreferredModelVariantAlias);
    }
}
