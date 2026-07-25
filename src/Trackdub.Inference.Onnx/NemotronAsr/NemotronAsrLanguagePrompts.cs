using System.Text.Json;

namespace Trackdub.Inference.Onnx.NemotronAsr;

internal static class NemotronAsrLanguagePrompts
{
    public const long AutoPromptIndex = 101;

    private static readonly IReadOnlyDictionary<string, long> PromptIndices =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto"] = AutoPromptIndex,
            ["ar"] = 7,
            ["ar-AR"] = 7,
            ["bg"] = 30,
            ["bg-BG"] = 30,
            ["cs"] = 22,
            ["cs-CZ"] = 22,
            ["da"] = 25,
            ["da-DK"] = 25,
            ["de"] = 9,
            ["de-DE"] = 9,
            ["el"] = 21,
            ["el-GR"] = 21,
            ["en"] = 0,
            ["en-US"] = 0,
            ["en-GB"] = 1,
            ["es"] = 3,
            ["es-ES"] = 2,
            ["es-US"] = 3,
            ["et"] = 60,
            ["et-EE"] = 60,
            ["fi"] = 26,
            ["fi-FI"] = 26,
            ["fr"] = 8,
            ["fr-FR"] = 8,
            ["hi"] = 6,
            ["hi-IN"] = 6,
            ["hr"] = 29,
            ["hr-HR"] = 29,
            ["hu"] = 23,
            ["hu-HU"] = 23,
            ["it"] = 15,
            ["it-IT"] = 15,
            ["ja"] = 10,
            ["ja-JP"] = 10,
            ["ko"] = 14,
            ["ko-KR"] = 14,
            ["lt"] = 31,
            ["lt-LT"] = 31,
            ["lv"] = 61,
            ["lv-LV"] = 61,
            ["mt"] = 102,
            ["mt-MT"] = 102,
            ["nb"] = 103,
            ["nb-NO"] = 103,
            ["nn"] = 104,
            ["nn-NO"] = 104,
            ["nl"] = 16,
            ["nl-NL"] = 16,
            ["pl"] = 17,
            ["pl-PL"] = 17,
            ["pt"] = 13,
            ["pt-BR"] = 12,
            ["pt-PT"] = 13,
            ["ro"] = 20,
            ["ro-RO"] = 20,
            ["ru"] = 11,
            ["ru-RU"] = 11,
            ["sk"] = 28,
            ["sk-SK"] = 28,
            ["sl"] = 62,
            ["sl-SI"] = 62,
            ["sv"] = 24,
            ["sv-SE"] = 24,
            ["th"] = 32,
            ["th-TH"] = 32,
            ["tr"] = 18,
            ["tr-TR"] = 18,
            ["uk"] = 19,
            ["uk-UA"] = 19,
            ["vi"] = 33,
            ["vi-VN"] = 33,
            ["zh"] = 4,
            ["zh-CN"] = 4,
            ["zh-TW"] = 5,
        };

    public static async Task<NemotronAsrPromptDictionary> LoadAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        await using FileStream stream = File.OpenRead(configPath);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("prompt_dictionary", out JsonElement promptElement) ||
            promptElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Nemotron config.json did not contain a prompt_dictionary object.");
        }

        var promptIndices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in promptElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt64(out long promptIndex))
            {
                promptIndices[property.Name] = promptIndex;
            }
        }

        if (promptIndices.Count == 0)
        {
            throw new InvalidOperationException("Nemotron config.json prompt_dictionary did not contain any prompt indices.");
        }

        long autoPromptIndex = promptIndices.TryGetValue("auto", out long loadedAutoPromptIndex)
            ? loadedAutoPromptIndex
            : AutoPromptIndex;

        return new NemotronAsrPromptDictionary(promptIndices, autoPromptIndex);
    }

    public static long ResolvePromptIndex(string? language)
        => ResolvePromptIndex(PromptIndices, language, AutoPromptIndex);

    public static string? TryGetIsoCode(string? language)
        => TryGetIsoCode(PromptIndices, language, AutoPromptIndex);

    public static string? TryExtractLanguageTag(string decodedPiece)
    {
        string trimmed = decodedPiece.Trim();
        if (trimmed.Length < 4 || trimmed[0] != '<' || trimmed[^1] != '>')
        {
            return null;
        }

        string inner = trimmed[1..^1].Trim();
        int separatorIndex = inner.IndexOfAny(['-', '_']);
        string language = separatorIndex > 0 ? inner[..separatorIndex] : inner;
        return language.Length is >= 2 and <= 3 &&
               language.All(static character => character is >= 'a' and <= 'z')
            ? language
            : null;
    }

    internal static long ResolvePromptIndex(
        IReadOnlyDictionary<string, long> promptIndices,
        string? language,
        long autoPromptIndex)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return autoPromptIndex;
        }

        string normalized = language.Trim().Replace('_', '-');
        if (promptIndices.TryGetValue(normalized, out long promptIndex))
        {
            return promptIndex;
        }

        int separatorIndex = normalized.IndexOfAny(['-', '_']);
        if (separatorIndex > 0 &&
            promptIndices.TryGetValue(normalized[..separatorIndex], out promptIndex))
        {
            return promptIndex;
        }

        if (separatorIndex < 0)
        {
            KeyValuePair<string, long> firstLocaleMatch = promptIndices
                .FirstOrDefault(entry => entry.Key.StartsWith(normalized + "-", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(firstLocaleMatch.Key))
            {
                return firstLocaleMatch.Value;
            }
        }

        return autoPromptIndex;
    }

    internal static string? TryGetIsoCode(
        IReadOnlyDictionary<string, long> promptIndices,
        string? language,
        long autoPromptIndex)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        string normalized = language.Trim().Replace('_', '-');
        if (string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase) ||
            ResolvePromptIndex(promptIndices, normalized, autoPromptIndex) == autoPromptIndex)
        {
            return null;
        }

        int separatorIndex = normalized.IndexOfAny(['-', '_']);
        return (separatorIndex > 0 ? normalized[..separatorIndex] : normalized).ToLowerInvariant();
    }
}

internal sealed class NemotronAsrPromptDictionary(
    IReadOnlyDictionary<string, long> promptIndices,
    long autoPromptIndex)
{
    public long AutoPromptIndex { get; } = autoPromptIndex;

    public long ResolvePromptIndex(string? language) =>
        NemotronAsrLanguagePrompts.ResolvePromptIndex(promptIndices, language, AutoPromptIndex);

    public string? TryGetIsoCode(string? language) =>
        NemotronAsrLanguagePrompts.TryGetIsoCode(promptIndices, language, AutoPromptIndex);
}
