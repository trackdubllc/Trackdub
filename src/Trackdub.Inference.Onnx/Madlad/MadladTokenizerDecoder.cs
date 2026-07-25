using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Trackdub.Inference.Onnx.Madlad;

internal sealed class MadladTokenizerDecoder
{
    private readonly SentencePieceTokenizer tokenizer;

    private MadladTokenizerDecoder(
        SentencePieceTokenizer tokenizer,
        int decoderStartTokenId,
        int endOfSentenceTokenId,
        int padTokenId,
        int maxGenerationLength)
    {
        this.tokenizer = tokenizer;
        DecoderStartTokenId = decoderStartTokenId;
        EndOfSentenceTokenId = endOfSentenceTokenId;
        PadTokenId = padTokenId;
        MaxGenerationLength = maxGenerationLength;
    }

    public int DecoderStartTokenId { get; }

    public int EndOfSentenceTokenId { get; }

    public int PadTokenId { get; }

    public int MaxGenerationLength { get; }

    public static async Task<MadladTokenizerDecoder> LoadAsync(string modelRootPath)
    {
        string tokenizerPath = ResolveExistingPath(
            modelRootPath,
            "spiece.model",
            "tokenizer.model",
            "sentencepiece.model");
        string configPath = Path.Combine(modelRootPath, "config.json");
        string generationConfigPath = Path.Combine(modelRootPath, "generation_config.json");

        MadladTokenizerConfig config = await LoadConfigAsync(configPath, generationConfigPath).ConfigureAwait(false);
        using FileStream tokenizerStream = File.OpenRead(tokenizerPath);
        SentencePieceTokenizer tokenizer = SentencePieceTokenizer.Create(
            tokenizerStream,
            addBeginningOfSentence: false,
            addEndOfSentence: false);

        return new MadladTokenizerDecoder(
            tokenizer,
            config.DecoderStartTokenId,
            config.EndOfSentenceTokenId,
            config.PadTokenId,
            config.MaxGenerationLength);
    }

    public long[] EncodeSourceText(string text, string targetLanguageTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguageTag);
        return tokenizer
            .EncodeToIds($"{targetLanguageTag} {text.Trim()}")
            .Select(static tokenId => (long)tokenId)
            .ToArray();
    }

    public string DecodeTargetText(IEnumerable<long> tokenIds)
    {
        int[] decodedTokenIds = tokenIds
            .Where(tokenId => tokenId != DecoderStartTokenId &&
                              tokenId != EndOfSentenceTokenId &&
                              tokenId != PadTokenId)
            .Select(static tokenId => checked((int)tokenId))
            .ToArray();
        return tokenizer.Decode(decodedTokenIds).Trim();
    }

    private static async Task<MadladTokenizerConfig> LoadConfigAsync(string configPath, string generationConfigPath)
    {
        int decoderStartTokenId = 0;
        int endOfSentenceTokenId = 1;
        int padTokenId = 0;
        int maxGenerationLength = 256;

        if (File.Exists(configPath))
        {
            string configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(configText);
            JsonElement root = document.RootElement;
            decoderStartTokenId = ReadInt32(root, "decoder_start_token_id", decoderStartTokenId);
            endOfSentenceTokenId = ReadInt32(root, "eos_token_id", endOfSentenceTokenId);
            padTokenId = ReadInt32(root, "pad_token_id", padTokenId);
            maxGenerationLength = ReadInt32(root, "n_positions", maxGenerationLength);
        }

        if (File.Exists(generationConfigPath))
        {
            string genConfigText = await File.ReadAllTextAsync(generationConfigPath).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(genConfigText);
            JsonElement root = document.RootElement;
            maxGenerationLength = ReadInt32(root, "max_length", maxGenerationLength);
        }

        return new MadladTokenizerConfig(
            decoderStartTokenId,
            endOfSentenceTokenId,
            padTokenId,
            Math.Max(32, maxGenerationLength));
    }

    private static int ReadInt32(JsonElement root, string propertyName, int defaultValue)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return defaultValue;
        }

        return element.ValueKind is JsonValueKind.Number && element.TryGetInt32(out int value)
            ? value
            : defaultValue;
    }

    private static string ResolveExistingPath(string modelRootPath, params string[] fileNames)
    {
        foreach (string fileName in fileNames)
        {
            string candidatePath = Path.Combine(modelRootPath, fileName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new FileNotFoundException(
            $"The MADLAD tokenizer was not found under '{modelRootPath}'.",
            Path.Combine(modelRootPath, fileNames[0]));
    }

    private sealed record MadladTokenizerConfig(
        int DecoderStartTokenId,
        int EndOfSentenceTokenId,
        int PadTokenId,
        int MaxGenerationLength);
}
