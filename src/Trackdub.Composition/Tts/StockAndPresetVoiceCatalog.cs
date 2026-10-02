using System.Diagnostics.CodeAnalysis;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Composition.Tts;

/// <summary>
/// Voice catalog that keeps the stock voice pool (Kokoro) for automatic voice picking while also
/// resolving explicitly named preset voices from other engines, such as Qwen3 CustomVoice
/// (<c>qwen3:vivian</c>). Listing stays on the stock pool so default voice selection for
/// English and Spanish is unchanged; lookups by ID fall through to the preset catalogs.
/// </summary>
public sealed class StockAndPresetVoiceCatalog(IVoiceCatalog stockCatalog, params IVoiceCatalog[] presetCatalogs)
    : IVoiceCatalog
{
    private readonly IVoiceCatalog stockCatalog = stockCatalog ?? throw new ArgumentNullException(nameof(stockCatalog));
    private readonly IVoiceCatalog[] presetCatalogs = presetCatalogs ?? [];

    public IReadOnlyList<VoiceCatalogEntry> GetVoices(string? languageCode = null) =>
        stockCatalog.GetVoices(languageCode);

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
