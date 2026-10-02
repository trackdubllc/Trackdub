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
        string speakerIdsPath = Path.Join(modelRootDirectory, "embeddings", "speaker_ids.json");
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
            entries.Add(CreateEntry(property.Name));
        }

        return entries.Count == 0 ? KnownAvailable() : new Qwen3TtsVoiceCatalog(entries);
    }

    /// <summary>
    /// The nine CustomVoice presets shipped in both the 0.6B and 1.7B CustomVoice checkpoints,
    /// with the gender and native language published on the upstream model card
    /// (https://huggingface.co/Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice). Every preset can speak all
    /// ten supported languages, so entries keep the "mul" language code and carry their native
    /// language in the display name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Gender, string NativeLanguage)> PresetMetadata =
        new Dictionary<string, (string Gender, string NativeLanguage)>(StringComparer.OrdinalIgnoreCase)
        {
            ["vivian"] = ("female", "Chinese"),
            ["serena"] = ("female", "Chinese"),
            ["uncle_fu"] = ("male", "Chinese"),
            ["dylan"] = ("male", "Chinese, Beijing dialect"),
            ["eric"] = ("male", "Chinese, Sichuan dialect"),
            ["ryan"] = ("male", "English"),
            ["aiden"] = ("male", "English"),
            ["ono_anna"] = ("female", "Japanese"),
            ["sohee"] = ("female", "Korean"),
        };

    public static Qwen3TtsVoiceCatalog KnownAvailable() =>
        new(PresetMetadata.Keys.Select(CreateEntry).ToArray());

    private static VoiceCatalogEntry CreateEntry(string speaker)
    {
        string displayName = ToDisplayName(speaker);
        return PresetMetadata.TryGetValue(speaker, out (string Gender, string NativeLanguage) metadata)
            ? new VoiceCatalogEntry($"qwen3:{speaker}", "mul", metadata.Gender, $"{displayName} ({metadata.NativeLanguage})")
            : new VoiceCatalogEntry($"qwen3:{speaker}", "mul", "unknown", displayName);
    }

    private static string ToDisplayName(string speaker) =>
        string.Join(' ', speaker.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(static token => char.ToUpperInvariant(token[0]) + token[1..]));
}
