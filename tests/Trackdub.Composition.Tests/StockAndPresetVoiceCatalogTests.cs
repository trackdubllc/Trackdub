using Trackdub.Composition.Tts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Qwen3Tts;
using System.Diagnostics.CodeAnalysis;

namespace Trackdub.Composition.Tests;

public sealed class StockAndPresetVoiceCatalogTests
{
    [Fact]
    public void GetVoices_lists_only_the_stock_pool()
    {
        var stock = new StockCatalog();
        var catalog = new StockAndPresetVoiceCatalog(stock, Qwen3TtsVoiceCatalog.KnownAvailable());

        Assert.Equal(stock.GetVoices().Count, catalog.GetVoices().Count);
        Assert.DoesNotContain(catalog.GetVoices(), voice => voice.VoiceId.StartsWith("qwen3:", StringComparison.Ordinal));
    }

    [Fact]
    public void TryGetVoice_resolves_stock_and_preset_ids()
    {
        var catalog = new StockAndPresetVoiceCatalog(new StockCatalog(), Qwen3TtsVoiceCatalog.KnownAvailable());

        Assert.True(catalog.TryGetVoice("af_heart", out VoiceCatalogEntry? stock));
        Assert.Equal("Heart", stock.DisplayName);
        Assert.True(catalog.TryGetVoice("qwen3:serena", out VoiceCatalogEntry? preset));
        Assert.Equal("female", preset.Gender);
        Assert.False(catalog.TryGetVoice("qwen3:nobody", out _));
    }

    private sealed class StockCatalog : IVoiceCatalog
    {
        private readonly VoiceCatalogEntry[] voices =
        [
            new("af_heart", "en-us", "female", "Heart"),
            new("ef_dora", "es", "female", "Dora"),
        ];

        public IReadOnlyList<VoiceCatalogEntry> GetVoices(string? languageCode = null) => voices;

        public bool TryGetVoice(string voiceId, [NotNullWhen(true)] out VoiceCatalogEntry? entry)
        {
            entry = voices.FirstOrDefault(voice => voice.VoiceId == voiceId);
            return entry is not null;
        }
    }
}
