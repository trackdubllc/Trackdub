using System.Text.Json;

namespace Trackdub.Inference.Onnx.Kokoro;

internal sealed class KokoroTokenizer
{
    private const long BosEosTokenId = 0; // "$" wraps the sequence per post-processor
    private const int MaxSequenceLength = 512;

    private readonly IReadOnlyDictionary<string, int> vocab;

    private KokoroTokenizer(IReadOnlyDictionary<string, int> vocab)
    {
        this.vocab = vocab;
    }

    public static async Task<KokoroTokenizer> LoadAsync(string modelRootPath)
    {
        string tokenizerPath = Path.Combine(modelRootPath, "tokenizer.json");
        if (!File.Exists(tokenizerPath))
        {
            throw new FileNotFoundException("Kokoro tokenizer.json not found.", tokenizerPath);
        }

        string tokenizerText = await File.ReadAllTextAsync(tokenizerPath).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(tokenizerText);
        JsonElement vocabElement = document.RootElement
            .GetProperty("model")
            .GetProperty("vocab");

        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty entry in vocabElement.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt32(out int tokenId))
            {
                vocab[entry.Name] = tokenId;
            }
        }

        return new KokoroTokenizer(vocab);
    }

    // Returns [BOS, tok1, ..., tokN, EOS], capped at MaxSequenceLength.
    // Unknown characters are silently skipped per the tokenizer's normalizer behavior.
    public long[] Encode(string phonemes)
    {
        var tokens = new List<long>(phonemes.Length + 2) { BosEosTokenId };

        foreach (char c in phonemes)
        {
            string key = c.ToString();
            if (vocab.TryGetValue(key, out int tokenId))
            {
                tokens.Add(tokenId);
                if (tokens.Count == MaxSequenceLength - 1)
                {
                    break;
                }
            }
        }

        tokens.Add(BosEosTokenId);
        return [.. tokens];
    }
}
