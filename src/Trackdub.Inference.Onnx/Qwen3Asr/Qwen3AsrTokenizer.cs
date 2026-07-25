using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Trackdub.Inference.Onnx.Qwen3Asr;

internal sealed class Qwen3AsrTokenizer
{
    private readonly BpeTokenizer tokenizer;

    private Qwen3AsrTokenizer(BpeTokenizer tokenizer)
    {
        this.tokenizer = tokenizer;
    }

    public static async Task<Qwen3AsrTokenizer> LoadAsync(string modelRootPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRootPath);
        string tokenizerPath = Path.Combine(modelRootPath, "tokenizer.json");
        string tokenizerText = await File.ReadAllTextAsync(tokenizerPath, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(tokenizerText);
        JsonElement root = document.RootElement;
        JsonElement model = root.GetProperty("model");
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty entry in model.GetProperty("vocab").EnumerateObject())
        {
            vocabulary[entry.Name] = entry.Value.GetInt32();
        }

        var merges = new List<string>();
        foreach (JsonElement merge in model.GetProperty("merges").EnumerateArray())
        {
            if (merge.ValueKind is JsonValueKind.String)
            {
                merges.Add(merge.GetString() ?? string.Empty);
                continue;
            }

            if (merge.ValueKind is JsonValueKind.Array)
            {
                string? first = merge[0].GetString();
                string? second = merge[1].GetString();
                if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(second))
                {
                    merges.Add($"{first} {second}");
                }
            }
        }

        Dictionary<string, int> specialTokens = ReadSpecialTokens(root);
        string addedTokensPath = Path.Combine(modelRootPath, "added_tokens.json");
        if (File.Exists(addedTokensPath))
        {
            using JsonDocument addedDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(addedTokensPath, cancellationToken).ConfigureAwait(false));
            if (addedDocument.RootElement.ValueKind is JsonValueKind.Array)
            {
                MergeSpecialTokens(specialTokens, addedDocument.RootElement);
            }
        }

        foreach (KeyValuePair<string, int> special in specialTokens)
        {
            vocabulary.TryAdd(special.Key, special.Value);
        }

        const string unknownToken = "<|endoftext|>";
        var options = new BpeOptions(vocabulary)
        {
            Merges = merges,
            SpecialTokens = specialTokens,
            ByteLevel = true,
        };
        if (vocabulary.ContainsKey(unknownToken))
        {
            options.UnknownToken = unknownToken;
        }

        return new Qwen3AsrTokenizer(BpeTokenizer.Create(options));
    }

    public IReadOnlyList<int> Encode(string text) =>
        tokenizer.EncodeToIds(text.Trim());

    public string Decode(IReadOnlyList<int> tokenIds) =>
        tokenizer.Decode(tokenIds);

    private static Dictionary<string, int> ReadSpecialTokens(JsonElement root)
    {
        var tokens = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty("added_tokens", out JsonElement addedTokens) ||
            addedTokens.ValueKind is not JsonValueKind.Array)
        {
            return tokens;
        }

        MergeSpecialTokens(tokens, addedTokens);
        return tokens;
    }

    private static void MergeSpecialTokens(Dictionary<string, int> destination, JsonElement addedTokens)
    {
        foreach (JsonElement token in addedTokens.EnumerateArray())
        {
            if (!token.TryGetProperty("content", out JsonElement contentElement) ||
                !token.TryGetProperty("id", out JsonElement idElement) ||
                contentElement.ValueKind is not JsonValueKind.String ||
                idElement.ValueKind is not JsonValueKind.Number ||
                !idElement.TryGetInt32(out int id))
            {
                continue;
            }

            string? content = contentElement.GetString();
            if (!string.IsNullOrWhiteSpace(content))
            {
                destination[content] = id;
            }
        }
    }
}
