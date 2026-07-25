using System.Diagnostics.CodeAnalysis;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeVoiceCatalog(IReadOnlyList<VoiceCatalogEntry>? voices = null) : IVoiceCatalog
{
    private readonly IReadOnlyList<VoiceCatalogEntry> voices = voices ?? DefaultVoices();

    public IReadOnlyList<VoiceCatalogEntry> GetVoices(string? languageCode = null) =>
        languageCode is null
            ? voices
            : voices.Where(v => v.LanguageCode == languageCode).ToList();

    public bool TryGetVoice(string voiceId, [NotNullWhen(true)] out VoiceCatalogEntry? entry)
    {
        entry = voices.FirstOrDefault(v => v.VoiceId == voiceId);
        return entry is not null;
    }

    private static IReadOnlyList<VoiceCatalogEntry> DefaultVoices() =>
    [
        new("af_heart", "mul",   "female", "Heart"),
        new("am_adam",  "mul",   "male",   "Adam"),
        new("bf_alice", "en-gb", "female", "Alice")
    ];
}
