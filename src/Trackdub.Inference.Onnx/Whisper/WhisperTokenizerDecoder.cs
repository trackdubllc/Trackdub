using System.Text;
using System.Text.Json;

namespace Trackdub.Inference.Onnx.Whisper;

internal sealed class WhisperTokenizerDecoder
{
    private readonly IReadOnlyDictionary<int, string> tokenTextById;
    private readonly IReadOnlySet<int> suppressedTokens;
    private readonly IReadOnlySet<int> languageTokenIds;
    private readonly IReadOnlyDictionary<char, byte> byteDecoder;
    private readonly int decoderStartToken;
    private readonly int endOfTranscriptToken;
    private readonly int? transcribeToken;
    private readonly int? noTimestampsToken;
    private readonly int timestampBeginToken;

    private WhisperTokenizerDecoder(
        IReadOnlyDictionary<int, string> tokenTextById,
        IReadOnlySet<int> suppressedTokens,
        IReadOnlySet<int> languageTokenIds,
        IReadOnlyDictionary<char, byte> byteDecoder,
        int decoderStartToken,
        int endOfTranscriptToken,
        int? transcribeToken,
        int? noTimestampsToken,
        int timestampBeginToken)
    {
        this.tokenTextById = tokenTextById;
        this.suppressedTokens = suppressedTokens;
        this.languageTokenIds = languageTokenIds;
        this.byteDecoder = byteDecoder;
        this.decoderStartToken = decoderStartToken;
        this.endOfTranscriptToken = endOfTranscriptToken;
        this.transcribeToken = transcribeToken;
        this.noTimestampsToken = noTimestampsToken;
        this.timestampBeginToken = timestampBeginToken;
    }

    public int DecoderStartToken => decoderStartToken;

    public int EndOfTranscriptToken => endOfTranscriptToken;

    public int TimestampBeginToken => timestampBeginToken;

    public IReadOnlySet<int> SuppressedTokens => suppressedTokens;

    public IReadOnlySet<int> LanguageTokenIds => languageTokenIds;

    public static async Task<WhisperTokenizerDecoder> LoadAsync(string modelRootPath)
    {
        string tokenizerConfigPath = Path.Combine(modelRootPath, "tokenizer_config.json");
        string vocabPath = Path.Combine(modelRootPath, "vocab.json");
        string configPath = Path.Combine(modelRootPath, "config.json");

        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException("Whisper vocabulary was not found.", vocabPath);
        }

        Dictionary<int, string> tokenTextById = await LoadTokenTextsAsync(vocabPath).ConfigureAwait(false);
        WhisperModelConfig config = await LoadConfigAsync(configPath).ConfigureAwait(false);
        int timestampBeginToken = tokenTextById
            .Where(static pair => pair.Value.Equals("<|0.00|>", StringComparison.Ordinal))
            .Select(static pair => pair.Key)
            .DefaultIfEmpty(50364)
            .First();

        var suppressed = new HashSet<int>(config.SuppressTokens);
        foreach (int token in config.BeginSuppressTokens)
        {
            suppressed.Add(token);
        }

        if (File.Exists(tokenizerConfigPath))
        {
            string tokenizerConfigText = await File.ReadAllTextAsync(tokenizerConfigPath).ConfigureAwait(false);
            using JsonDocument tokenizerConfig = JsonDocument.Parse(tokenizerConfigText);
            if (tokenizerConfig.RootElement.TryGetProperty("added_tokens_decoder", out JsonElement decoderElement))
            {
                foreach (JsonProperty property in decoderElement.EnumerateObject())
                {
                    if (!int.TryParse(property.Name, out int tokenId))
                    {
                        continue;
                    }

                    if (property.Value.TryGetProperty("content", out JsonElement contentElement))
                    {
                        tokenTextById[tokenId] = contentElement.GetString() ?? string.Empty;
                    }
                }
            }
        }

        HashSet<int> languageTokenIds = tokenTextById
            .Where(static pair => IsLanguageTokenText(pair.Value))
            .Select(static pair => pair.Key)
            .ToHashSet();

        int? transcribeToken = FindTokenId(tokenTextById, "<|transcribe|>");
        int? noTimestampsToken = FindTokenId(tokenTextById, "<|notimestamps|>");

        return new WhisperTokenizerDecoder(
            tokenTextById,
            suppressed,
            languageTokenIds,
            BuildByteDecoder(),
            config.DecoderStartTokenId,
            config.EndOfTranscriptTokenId,
            transcribeToken,
            noTimestampsToken,
            timestampBeginToken);
    }

    public IReadOnlyList<int> BuildTranscriptionPrompt(int? languageTokenId)
    {
        var promptTokens = new List<int>(capacity: 4)
        {
            decoderStartToken
        };

        if (languageTokenId is int languageToken &&
            languageTokenIds.Contains(languageToken))
        {
            promptTokens.Add(languageToken);
        }

        if (transcribeToken is int taskToken)
        {
            promptTokens.Add(taskToken);
        }

        if (noTimestampsToken is int timestampsToken)
        {
            promptTokens.Add(timestampsToken);
        }

        return promptTokens;
    }

    public string? TryGetLanguageCode(int? languageTokenId)
    {
        if (languageTokenId is not int tokenId ||
            !languageTokenIds.Contains(tokenId) ||
            !tokenTextById.TryGetValue(tokenId, out string? tokenText) ||
            !IsLanguageTokenText(tokenText))
        {
            return null;
        }

        return tokenText[2..^2];
    }

    public int? TryGetLanguageTokenId(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        string tokenText = $"<|{languageCode.Trim().ToLowerInvariant()}|>";
        return FindTokenId(tokenTextById, tokenText);
    }

    public string DecodeText(IEnumerable<int> tokenIds)
    {
        var bytes = new List<byte>();
        foreach (int tokenId in tokenIds)
        {
            if (tokenId >= timestampBeginToken || tokenId == endOfTranscriptToken)
            {
                continue;
            }

            if (!tokenTextById.TryGetValue(tokenId, out string? tokenText))
            {
                continue;
            }

            if (tokenText.StartsWith("<|", StringComparison.Ordinal) &&
                tokenText.EndsWith("|>", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (char ch in tokenText)
            {
                if (byteDecoder.TryGetValue(ch, out byte decodedByte))
                {
                    bytes.Add(decodedByte);
                }
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    private static async Task<Dictionary<int, string>> LoadTokenTextsAsync(string vocabPath)
    {
        string vocabText = await File.ReadAllTextAsync(vocabPath).ConfigureAwait(false);
        using JsonDocument vocabDocument = JsonDocument.Parse(vocabText);
        var tokenTextById = new Dictionary<int, string>();
        foreach (JsonProperty property in vocabDocument.RootElement.EnumerateObject())
        {
            int tokenId = property.Value.GetInt32();
            tokenTextById[tokenId] = property.Name;
        }

        return tokenTextById;
    }

    private static async Task<WhisperModelConfig> LoadConfigAsync(string configPath)
    {
        string configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(configText);
        JsonElement root = document.RootElement;

        int decoderStartTokenId = root.GetProperty("decoder_start_token_id").GetInt32();
        int endOfTranscriptTokenId = root.GetProperty("eos_token_id").GetInt32();

        List<int> suppressTokens = root.TryGetProperty("suppress_tokens", out JsonElement suppressElement) &&
                                   suppressElement.ValueKind is JsonValueKind.Array
            ? suppressElement.EnumerateArray().Select(static element => element.GetInt32()).ToList()
            : [];

        List<int> beginSuppressTokens = root.TryGetProperty("begin_suppress_tokens", out JsonElement beginSuppressElement) &&
                                        beginSuppressElement.ValueKind is JsonValueKind.Array
            ? beginSuppressElement.EnumerateArray().Select(static element => element.GetInt32()).ToList()
            : [];

        return new WhisperModelConfig(
            decoderStartTokenId,
            endOfTranscriptTokenId,
            suppressTokens,
            beginSuppressTokens);
    }

    private static int? FindTokenId(IReadOnlyDictionary<int, string> tokenTextById, string tokenText)
    {
        foreach ((int tokenId, string value) in tokenTextById)
        {
            if (value.Equals(tokenText, StringComparison.Ordinal))
            {
                return tokenId;
            }
        }

        return null;
    }

    private static bool IsLanguageTokenText(string tokenText)
    {
        if (!tokenText.StartsWith("<|", StringComparison.Ordinal) ||
            !tokenText.EndsWith("|>", StringComparison.Ordinal))
        {
            return false;
        }

        string inner = tokenText[2..^2];
        return inner.Length is >= 2 and <= 5 &&
               inner.All(static ch => ch is >= 'a' and <= 'z');
    }

    private static IReadOnlyDictionary<char, byte> BuildByteDecoder()
    {
        List<int> byteValues =
        [
            .. Enumerable.Range(33, 94),
            .. Enumerable.Range(161, 12),
            .. Enumerable.Range(174, 82)
        ];

        List<int> unicodeValues = [.. byteValues];
        int extra = 0;
        for (int byteValue = 0; byteValue < 256; byteValue++)
        {
            if (byteValues.Contains(byteValue))
            {
                continue;
            }

            byteValues.Add(byteValue);
            unicodeValues.Add(256 + extra);
            extra++;
        }

        var decoder = new Dictionary<char, byte>(byteValues.Count);
        for (int index = 0; index < byteValues.Count; index++)
        {
            decoder[(char)unicodeValues[index]] = (byte)byteValues[index];
        }

        return decoder;
    }

    private sealed record WhisperModelConfig(
        int DecoderStartTokenId,
        int EndOfTranscriptTokenId,
        IReadOnlyList<int> SuppressTokens,
        IReadOnlyList<int> BeginSuppressTokens);
}
