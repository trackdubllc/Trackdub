using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubDubbingEngineLanguageNormalizationTests
{
    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("pt-BR", "pt")]
    [InlineData("zh-Hant-TW", "zh")]
    [InlineData("fr", "fr")]
    [InlineData(null, null)]
    public void NormalizeAsrSourceLanguageCode_strips_region_tags(string? sourceLanguageCode, string? expected)
    {
        Assert.Equal(expected, TrackdubDubbingEngine.NormalizeAsrSourceLanguageCode(sourceLanguageCode));
    }
}
