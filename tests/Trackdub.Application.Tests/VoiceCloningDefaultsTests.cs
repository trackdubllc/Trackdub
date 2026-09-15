using Trackdub.Application.Transcripts;

namespace Trackdub.Application.Tests;

/// <summary>
/// Guards the language-aware clone-model selection. English targets use the
/// English-only turbo model; every other language must route to the multilingual
/// model, because the turbo/base models would otherwise synthesize English-sounding
/// audio for a non-English target (a fake-readiness failure).
/// </summary>
public sealed class VoiceCloningDefaultsTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("en-US")]
    public void ResolveDefaultChatterboxAlias_ForEnglish_SelectsTurbo(string targetLanguage)
    {
        Assert.Equal(
            VoiceCloningDefaults.ChatterboxPrimaryAlias,
            VoiceCloningDefaults.ResolveDefaultChatterboxAlias(targetLanguage));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("ja")]
    [InlineData("ar")]
    [InlineData("sw")]
    [InlineData("ko")]
    [InlineData("PT")]
    [InlineData("es-ES")]
    public void ResolveDefaultChatterboxAlias_ForNonEnglish_SelectsMultilingual(string targetLanguage)
    {
        Assert.Equal(
            VoiceCloningDefaults.ChatterboxMultilingualAlias,
            VoiceCloningDefaults.ResolveDefaultChatterboxAlias(targetLanguage));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDefaultChatterboxAlias_WhenUnset_DefaultsToTurbo(string? targetLanguage)
    {
        // No target language is treated as English (turbo) rather than silently
        // selecting a multilingual model with an unknown language.
        Assert.Equal(
            VoiceCloningDefaults.ChatterboxPrimaryAlias,
            VoiceCloningDefaults.ResolveDefaultChatterboxAlias(targetLanguage));
    }

    [Theory]
    [InlineData(VoiceCloningDefaults.ChatterboxPrimaryAlias)]
    [InlineData(VoiceCloningDefaults.ChatterboxFallbackAlias)]
    [InlineData(VoiceCloningDefaults.ChatterboxMultilingualAlias)]
    [InlineData(VoiceCloningDefaults.CosyVoicePrimaryAlias)]
    [InlineData(VoiceCloningDefaults.CosyVoiceFallbackAlias)]
    [InlineData("CHATTERBOX-TURBO-ONNX")]
    [InlineData("  cosyvoice-300m  ")]
    public void IsVoiceCloningModelAlias_ForChatterboxAndCosyVoiceAliases_ReturnsTrue(string alias) =>
        Assert.True(VoiceCloningDefaults.IsVoiceCloningModelAlias(alias));

    [Theory]
    [InlineData("af_heart")]
    [InlineData("kokoro-onnx")]
    [InlineData(Qwen3TtsDefaults.Base06Alias)]
    [InlineData("f5tts-onnx")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsVoiceCloningModelAlias_ForStockOrOtherAliases_ReturnsFalse(string? alias) =>
        Assert.False(VoiceCloningDefaults.IsVoiceCloningModelAlias(alias));

    [Theory]
    [InlineData("f5")]
    [InlineData("f5tts")]
    [InlineData("f5tts-onnx")]
    [InlineData("f5-tts")]
    [InlineData("f5-tts-onnx")]
    [InlineData("swivid-f5-tts")]
    [InlineData("  F5TTS-ONNX  ")]
    public void IsF5VoiceCloningModelAlias_ForF5Aliases_ReturnsTrue(string alias) =>
        Assert.True(VoiceCloningDefaults.IsF5VoiceCloningModelAlias(alias));

    [Theory]
    [InlineData("af_heart")]
    [InlineData(VoiceCloningDefaults.ChatterboxPrimaryAlias)]
    [InlineData(null)]
    [InlineData("   ")]
    public void IsF5VoiceCloningModelAlias_ForNonF5Aliases_ReturnsFalse(string? alias) =>
        Assert.False(VoiceCloningDefaults.IsF5VoiceCloningModelAlias(alias));

    // The consolidated predicate is the single source of truth shared by
    // StartTtsStageHandler.IsVoiceCloningAlias and TtsOrchestrationService's non-clone
    // substitution guard. It must recognize the FULL clone-only alias set so the two cannot drift.
    [Theory]
    [InlineData(VoiceCloningDefaults.ChatterboxPrimaryAlias)]
    [InlineData(VoiceCloningDefaults.ChatterboxFallbackAlias)]
    [InlineData(VoiceCloningDefaults.ChatterboxMultilingualAlias)]
    [InlineData(VoiceCloningDefaults.CosyVoicePrimaryAlias)]
    [InlineData(VoiceCloningDefaults.CosyVoiceFallbackAlias)]
    [InlineData(Qwen3TtsDefaults.Base06Alias)]
    [InlineData(Qwen3TtsDefaults.Base17Alias)]
    [InlineData("f5")]
    [InlineData("f5tts")]
    [InlineData("f5tts-onnx")]
    [InlineData("f5-tts")]
    [InlineData("f5-tts-onnx")]
    [InlineData("swivid-f5-tts")]
    public void IsCloneOnlyModelAlias_ForEveryCloneAlias_ReturnsTrue(string alias) =>
        Assert.True(VoiceCloningDefaults.IsCloneOnlyModelAlias(alias));

    [Theory]
    [InlineData("af_heart")]
    [InlineData("kokoro-onnx")]
    [InlineData(Qwen3TtsDefaults.CustomVoice06Alias)]
    [InlineData(Qwen3TtsDefaults.CustomVoice17Alias)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCloneOnlyModelAlias_ForStockOrCustomVoiceAliases_ReturnsFalse(string? alias) =>
        Assert.False(VoiceCloningDefaults.IsCloneOnlyModelAlias(alias));
}
