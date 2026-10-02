using Trackdub.Application.Transcripts;

namespace Trackdub.Application.Tests;

public sealed class Qwen3TtsPresetDefaultsTests
{
    [Theory]
    [InlineData("zh", "qwen3:vivian")]
    [InlineData("zh-Hans", "qwen3:vivian")]
    [InlineData("ja", "qwen3:ono_anna")]
    [InlineData("ko", "qwen3:sohee")]
    [InlineData("en", "qwen3:ryan")]
    [InlineData("fr", "qwen3:ryan")]
    [InlineData(null, "qwen3:ryan")]
    public void ResolveDefaultPresetVoiceId_PrefersANativeSpeaker(string? targetLanguage, string expectedVoiceId) =>
        Assert.Equal(expectedVoiceId, Qwen3TtsDefaults.ResolveDefaultPresetVoiceId(targetLanguage));

    [Theory]
    [InlineData("qwen3:vivian", true)]
    [InlineData("QWEN3:Uncle_Fu", true)]
    [InlineData(" qwen3:ryan ", true)]
    [InlineData("qwen3:", false)]
    [InlineData("af_heart", false)]
    [InlineData(null, false)]
    public void IsPresetVoiceId_RecognizesQwen3PresetIds(string? voiceId, bool expected) =>
        Assert.Equal(expected, Qwen3TtsDefaults.IsPresetVoiceId(voiceId));
}
