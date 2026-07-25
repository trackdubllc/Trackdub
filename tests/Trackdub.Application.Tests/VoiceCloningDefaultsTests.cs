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
}
