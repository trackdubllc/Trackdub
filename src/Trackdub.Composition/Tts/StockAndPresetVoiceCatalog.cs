using System.Diagnostics.CodeAnalysis;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Composition.Tts;

/// <summary>
/// Voice catalog that keeps the stock voice pool (Kokoro) for automatic voice picking while also
/// resolving explicitly named preset voices from other engines, such as Qwen3 CustomVoice
/// (<c>qwen3:vivian</c>). Listing stays on the stock pool when no target language is set or when
/// Kokoro covers the target (English and Spanish). For other languages, preset voices are merged
/// into the list so interactive pickers can offer Qwen3 stock voices; lookups by ID always fall
/// through to the preset catalogs.
/// </summary>
public sealed class StockAndPresetVoiceCatalog(IVoiceCatalog stockCatalog, params IVoiceCatalog[] presetCatalogs)
    : IVoiceCatalog
{
    private readonly IVoiceCatalog stockCatalog = stockCatalog ?? throw new ArgumentNullException(nameof(stockCatalog));
    private readonly IVoiceCatalog[] presetCatalogs = presetCatalogs ?? [];

    public IReadOnlyList<VoiceCatalogEntry> GetVoices(string? languageCode = null)
    {
        IReadOnlyList<VoiceCatalogEntry> stock = stockCatalog.GetVoices(languageCode);
        if (string.IsNullOrWhiteSpace(languageCode) ||
            StockTtsVoiceMatcher.SupportsKokoro(languageCode) ||
            !Qwen3TtsDefaults.SupportsLanguage(languageCode))
        {
            return stock;
        }

        if (presetCatalogs.Length == 0)
        {
            return stock;
        }

        // Preset entries are tagged "mul". Stamp the requested language so a picker that
        // filters by target language can offer them without treating every mul voice as a match.
        string stampedLanguage = languageCode.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        var merged = new List<VoiceCatalogEntry>(stock);
        foreach (IVoiceCatalog presetCatalog in presetCatalogs)
        {
            merged.AddRange(presetCatalog.GetVoices(languageCode)
                .Select(voice => voice with { LanguageCode = stampedLanguage }));
        }

        return merged;
    }

    public bool TryGetVoice(string voiceId, [NotNullWhen(true)] out VoiceCatalogEntry? entry)
    {
        if (stockCatalog.TryGetVoice(voiceId, out entry))
        {
            return true;
        }

        foreach (IVoiceCatalog presetCatalog in presetCatalogs)
        {
            if (presetCatalog.TryGetVoice(voiceId, out entry))
            {
                return true;
            }
        }

        entry = null;
        return false;
    }
}
