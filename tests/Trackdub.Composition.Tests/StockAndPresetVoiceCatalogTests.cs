using Trackdub.Composition.Tts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Qwen3Tts;
using System.Diagnostics.CodeAnalysis;

namespace Trackdub.Composition.Tests;

public sealed class StockAndPresetVoiceCatalogTests
{
    [Fact]
    public void GetVoices_without_language_lists_only_the_stock_pool()
    {
        var stock = new StockCatalog();
        var catalog = new StockAndPresetVoiceCatalog(stock, Qwen3TtsVoiceCatalog.KnownAvailable());

        Assert.Equal(stock.GetVoices().Count, catalog.GetVoices().Count);
        Assert.DoesNotContain(catalog.GetVoices(), voice => voice.VoiceId.StartsWith("qwen3:", StringComparison.Ordinal));
    }

    [Fact]
    public void GetVoices_for_kokoro_language_lists_only_the_stock_pool()
    {
        var stock = new StockCatalog();
        var catalog = new StockAndPresetVoiceCatalog(stock, Qwen3TtsVoiceCatalog.KnownAvailable());

        IReadOnlyList<VoiceCatalogEntry> enVoices = catalog.GetVoices("en-us");
        Assert.Equal(stock.GetVoices("en-us").Count, enVoices.Count);
        Assert.DoesNotContain(enVoices, voice => voice.VoiceId.StartsWith("qwen3:", StringComparison.Ordinal));
    }

    [Fact]
    public void GetVoices_for_non_kokoro_language_merges_qwen3_presets()
    {
        var stock = new StockCatalog();
        var catalog = new StockAndPresetVoiceCatalog(stock, Qwen3TtsVoiceCatalog.KnownAvailable());

        Assert.Empty(stock.GetVoices("zh"));
        IReadOnlyList<VoiceCatalogEntry> zhVoices = catalog.GetVoices("zh");
        Assert.Equal(Qwen3TtsVoiceCatalog.KnownAvailable().GetVoices("zh").Count, zhVoices.Count);
        Assert.All(zhVoices, voice => Assert.StartsWith("qwen3:", voice.VoiceId, StringComparison.Ordinal));
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

        public IReadOnlyList<VoiceCatalogEntry> GetVoices(string? languageCode = null) =>
            string.IsNullOrWhiteSpace(languageCode)
                ? voices
                : voices
                    .Where(voice => voice.LanguageCode.StartsWith(
                        languageCode.Split('-')[0],
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();

        public bool TryGetVoice(string voiceId, [NotNullWhen(true)] out VoiceCatalogEntry? entry)
        {
            entry = voices.FirstOrDefault(voice => voice.VoiceId == voiceId);
            return entry is not null;
        }
    }
}
