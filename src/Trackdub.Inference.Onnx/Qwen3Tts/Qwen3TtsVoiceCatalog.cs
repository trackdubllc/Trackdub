using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Onnx.Qwen3Tts;

public sealed class Qwen3TtsVoiceCatalog : IVoiceCatalog
{
    private readonly IReadOnlyList<VoiceCatalogEntry> voices;

    private Qwen3TtsVoiceCatalog(IReadOnlyList<VoiceCatalogEntry> voices)
    {
        this.voices = voices;
    }

    public IReadOnlyList<VoiceCatalogEntry> GetVoices(string? languageCode = null)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return voices;
        }

        string normalized = languageCode.Trim().Split('-')[0].ToLowerInvariant();
        return voices
            .Where(voice => voice.LanguageCode.Equals("mul", StringComparison.OrdinalIgnoreCase) ||
                            voice.LanguageCode.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public bool TryGetVoice(string voiceId, [NotNullWhen(true)] out VoiceCatalogEntry? entry)
    {
        entry = voices.FirstOrDefault(voice =>
            voice.VoiceId.Equals(voiceId, StringComparison.OrdinalIgnoreCase));
        return entry is not null;
    }

    public static Qwen3TtsVoiceCatalog Load(string modelRootDirectory)
    {
        string speakerIdsPath = Path.Combine(modelRootDirectory, "embeddings", "speaker_ids.json");
        if (!File.Exists(speakerIdsPath))
        {
            return KnownAvailable();
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(speakerIdsPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return KnownAvailable();
        }

        var entries = new List<VoiceCatalogEntry>();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            string speaker = property.Name;
            entries.Add(new VoiceCatalogEntry(
                $"qwen3:{speaker}",
                "mul",
                "unknown",
                ToDisplayName(speaker)));
        }

        return entries.Count == 0 ? KnownAvailable() : new Qwen3TtsVoiceCatalog(entries);
    }

    public static Qwen3TtsVoiceCatalog KnownAvailable() =>
        new([
            new VoiceCatalogEntry("qwen3:ryan", "mul", "unknown", "Ryan"),
            new VoiceCatalogEntry("qwen3:serena", "mul", "unknown", "Serena"),
            new VoiceCatalogEntry("qwen3:vivian", "mul", "unknown", "Vivian"),
        ]);

    private static string ToDisplayName(string speaker) =>
        string.Join(' ', speaker.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(static token => char.ToUpperInvariant(token[0]) + token[1..]));
}
