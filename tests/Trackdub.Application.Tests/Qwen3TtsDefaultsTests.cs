using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.Application.Tests;

public sealed class Qwen3TtsDefaultsTests
{
    [Theory]
    [InlineData("balanced", Qwen3TtsDefaults.CustomVoice06Alias)]
    [InlineData("quality", Qwen3TtsDefaults.CustomVoice17Alias)]
    public void ResolveCustomVoiceAlias_MapsTier(string tier, string expectedAlias) =>
        Assert.Equal(expectedAlias, Qwen3TtsDefaults.ResolveCustomVoiceAlias(tier));

    [Theory]
    [InlineData("balanced", Qwen3TtsDefaults.Base06Alias)]
    [InlineData("quality", Qwen3TtsDefaults.Base17Alias)]
    public void ResolveBaseAlias_MapsTier(string tier, string expectedAlias) =>
        Assert.Equal(expectedAlias, Qwen3TtsDefaults.ResolveBaseAlias(tier));

    [Fact]
    public void ResolvedTtsModelSelection_Qwen3Override_SelectsCloneOrPreset()
    {
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TtsModelOverride: TtsModelOverride.Qwen3Tts);

        ResolvedTtsModelSelection preset = ResolvedTtsModelSelection.Resolve(selections, requiresVoiceClone: false, "quality");
        Assert.Equal(Qwen3TtsDefaults.CustomVoice17Alias, preset.ModelAlias);
        Assert.True(preset.UsesQwen3CustomVoice);

        ResolvedTtsModelSelection clone = ResolvedTtsModelSelection.Resolve(selections, requiresVoiceClone: true, "balanced");
        Assert.Equal(Qwen3TtsDefaults.Base06Alias, clone.ModelAlias);
        Assert.True(clone.UsesQwen3Base);
    }
}
