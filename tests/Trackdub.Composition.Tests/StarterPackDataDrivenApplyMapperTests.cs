using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackDataDrivenApplyMapperTests
{
    [Fact]
    public void ToApplySettings_cloud_stages_only_maps_cloud_defaults()
    {
        var apply = new StarterPackApplyBlock(
            TierPreference: "balanced",
            StageAliases: null,
            Overrides: null,
            CloudStages: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["asr"] = "openai-whisper",
                ["translation"] = "openai-gpt",
                ["tts"] = "openai",
            });

        StarterPackApplySettings settings = StarterPackDataDrivenApplyMapper.ToApplySettings(apply);

        Assert.Equal(AsrModelOverride.OpenAiWhisper, settings.AsrModelOverride);
        Assert.Equal(TranslationModelOverride.OpenAiGpt, settings.TranslationModelOverride);
        Assert.Equal(TtsModelOverride.OpenAiTts, settings.TtsModelOverride);
    }

    [Fact]
    public void ToApplySettings_hybrid_merges_cloud_stages_with_explicit_overrides()
    {
        var apply = new StarterPackApplyBlock(
            TierPreference: "fast",
            StageAliases: null,
            Overrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["asr"] = "genai",
                ["translation"] = "auto",
                ["tts"] = "auto",
            },
            CloudStages: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["translation"] = "openai-gpt",
                ["tts"] = "openai",
            });

        StarterPackApplySettings settings = StarterPackDataDrivenApplyMapper.ToApplySettings(apply);

        Assert.Equal(AsrModelOverride.GenAi, settings.AsrModelOverride);
        Assert.Equal(TranslationModelOverride.OpenAiGpt, settings.TranslationModelOverride);
        Assert.Equal(TtsModelOverride.OpenAiTts, settings.TtsModelOverride);
    }
}
