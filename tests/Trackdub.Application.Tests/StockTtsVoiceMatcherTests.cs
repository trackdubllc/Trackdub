using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Tests;

public sealed class StockTtsVoiceMatcherTests
{
    private static readonly VoiceCatalogEntry[] Catalog =
    [
        new("am_adam", "en-us", "male", "Adam"),
        new("am_michael", "en-us", "male", "Michael"),
        new("af_bella", "en-us", "female", "Bella"),
        new("bm_george", "en-gb", "male", "George"),
        new("ef_dora", "es", "female", "Dora"),
        new("em_alex", "es", "male", "Alex"),
    ];

    [Fact]
    public void PickClosest_prefers_gender_then_unused_voice()
    {
        VoiceCatalogEntry? voice = StockTtsVoiceMatcher.PickClosest(
            Catalog,
            "en",
            "male",
            reservedVoiceIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "am_adam" });

        Assert.NotNull(voice);
        Assert.Equal("bm_george", voice.VoiceId);
    }

    [Fact]
    public void PickClosest_skips_reserved_voice_in_the_same_locale()
    {
        VoiceCatalogEntry[] catalog =
        [
            new("am_adam", "en-us", "male", "Adam"),
            new("am_michael", "en-us", "male", "Michael"),
        ];

        VoiceCatalogEntry? voice = StockTtsVoiceMatcher.PickClosest(
            catalog,
            "en-us",
            "male",
            reservedVoiceIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "am_adam" });

        Assert.NotNull(voice);
        Assert.Equal("am_michael", voice.VoiceId);
    }

    [Fact]
    public void PickClosest_falls_back_to_language_when_gender_missing_from_catalog()
    {
        VoiceCatalogEntry[] femaleOnly =
        [
            new("af_bella", "en-us", "female", "Bella"),
        ];

        VoiceCatalogEntry? voice = StockTtsVoiceMatcher.PickClosest(femaleOnly, "en", "male");

        Assert.NotNull(voice);
        Assert.Equal("af_bella", voice.VoiceId);
    }

    [Fact]
    public void PickClosest_prefers_exact_locale_when_target_has_region()
    {
        VoiceCatalogEntry? voice = StockTtsVoiceMatcher.PickClosest(Catalog, "en-gb", "male");

        Assert.NotNull(voice);
        Assert.Equal("bm_george", voice.VoiceId);
    }

    [Fact]
    public void PickClosest_matches_spanish_male()
    {
        VoiceCatalogEntry? voice = StockTtsVoiceMatcher.PickClosest(Catalog, "es", "male");

        Assert.NotNull(voice);
        Assert.Equal("em_alex", voice.VoiceId);
    }

    [Fact]
    public void PickClosest_treats_multilingual_voices_as_language_compatible()
    {
        VoiceCatalogEntry[] catalog =
        [
            new("af_heart", "mul", "female", "Heart"),
            new("am_adam", "mul", "male", "Adam"),
        ];

        VoiceCatalogEntry? voice = StockTtsVoiceMatcher.PickClosest(catalog, "es", "male");

        Assert.NotNull(voice);
        Assert.Equal("am_adam", voice.VoiceId);
    }

    [Fact]
    public void PickClosest_returns_null_when_no_language_match()
    {
        Assert.Null(StockTtsVoiceMatcher.PickClosest(Catalog, "ja", "female"));
    }

    [Fact]
    public void SupportsKokoro_en_and_es_only()
    {
        Assert.True(StockTtsVoiceMatcher.SupportsKokoro("en-us"));
        Assert.True(StockTtsVoiceMatcher.SupportsKokoro("es"));
        Assert.False(StockTtsVoiceMatcher.SupportsKokoro("ja"));
    }
}
