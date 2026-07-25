using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Trackdub.Inference.Onnx.OpusMt;

internal sealed class OpusTokenizerDecoder
{
    private readonly SentencePieceTokenizer sourceTokenizer;
    private readonly SentencePieceTokenizer targetTokenizer;
    private readonly IReadOnlyDictionary<string, int> modelIdByPiece;
    private readonly IReadOnlyDictionary<int, string> pieceByModelId;
    private readonly IReadOnlyDictionary<int, string> sourcePieceByTokenizerId;
    private readonly IReadOnlyDictionary<string, int> targetTokenizerIdByPiece;

    private OpusTokenizerDecoder(
        SentencePieceTokenizer sourceTokenizer,
        SentencePieceTokenizer targetTokenizer,
        IReadOnlyDictionary<string, int> modelIdByPiece,
        IReadOnlyDictionary<int, string> pieceByModelId,
        IReadOnlyDictionary<int, string> sourcePieceByTokenizerId,
        IReadOnlyDictionary<string, int> targetTokenizerIdByPiece,
        int decoderStartTokenId,
        int endOfSentenceTokenId,
        int padTokenId,
        int maxGenerationLength)
    {
        this.sourceTokenizer = sourceTokenizer;
        this.targetTokenizer = targetTokenizer;
        this.modelIdByPiece = modelIdByPiece;
        this.pieceByModelId = pieceByModelId;
        this.sourcePieceByTokenizerId = sourcePieceByTokenizerId;
        this.targetTokenizerIdByPiece = targetTokenizerIdByPiece;
        DecoderStartTokenId = decoderStartTokenId;
        EndOfSentenceTokenId = endOfSentenceTokenId;
        PadTokenId = padTokenId;
        MaxGenerationLength = maxGenerationLength;
        RequiresTargetLanguagePrefix = modelIdByPiece.Keys
            .Any(static piece => piece.StartsWith(">>", StringComparison.Ordinal) &&
                                 piece.EndsWith("<<", StringComparison.Ordinal));
    }

    public int DecoderStartTokenId { get; }

    public int EndOfSentenceTokenId { get; }

    public int PadTokenId { get; }

    public int MaxGenerationLength { get; }

    public static async Task<OpusTokenizerDecoder> LoadAsync(string modelRootPath)
    {
        string sourceTokenizerPath = ResolveExistingPath(modelRootPath, "source.spm", "source.model");
        string targetTokenizerPath = ResolveExistingPath(modelRootPath, "target.spm", "target.model");
        string vocabPath = Path.Combine(modelRootPath, "vocab.json");
        string configPath = Path.Combine(modelRootPath, "config.json");
        string generationConfigPath = Path.Combine(modelRootPath, "generation_config.json");

        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException("The Opus vocabulary mapping was not found.", vocabPath);
        }

        OpusTokenizerConfig config = await LoadConfigAsync(configPath, generationConfigPath).ConfigureAwait(false);
        using FileStream sourceStream = File.OpenRead(sourceTokenizerPath);
        using FileStream targetStream = File.OpenRead(targetTokenizerPath);
        IReadOnlyDictionary<string, int> modelIdByPiece = await LoadVocabularyAsync(vocabPath).ConfigureAwait(false);

        SentencePieceTokenizer sourceTokenizer = SentencePieceTokenizer.Create(
            sourceStream,
            addBeginningOfSentence: false,
            addEndOfSentence: true);
        SentencePieceTokenizer targetTokenizer = SentencePieceTokenizer.Create(
            targetStream,
            addBeginningOfSentence: false,
            addEndOfSentence: false);
        IReadOnlyDictionary<int, string> pieceByModelId = modelIdByPiece
            .ToDictionary(pair => pair.Value, pair => pair.Key);
        IReadOnlyDictionary<int, string> sourcePieceByTokenizerId = sourceTokenizer.Vocabulary
            .ToDictionary(pair => pair.Value, pair => pair.Key);

        return new OpusTokenizerDecoder(
            sourceTokenizer,
            targetTokenizer,
            modelIdByPiece,
            pieceByModelId,
            sourcePieceByTokenizerId,
            targetTokenizer.Vocabulary,
            config.DecoderStartTokenId,
            config.EndOfSentenceTokenId,
            config.PadTokenId,
            config.MaxGenerationLength);
    }

    public long[] EncodeSourceText(string text, string? targetLanguagePrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        IEnumerable<long> encoded = sourceTokenizer
            .EncodeToIds(text.Trim())
            .Select(MapSourceTokenizerIdToModelId)
            .Select(static tokenId => (long)tokenId);

        if (targetLanguagePrefix is not null &&
            modelIdByPiece.TryGetValue(targetLanguagePrefix, out int prefixModelId))
        {
            encoded = new long[] { (long)prefixModelId }.Concat(encoded);
        }

        return encoded.ToArray();
    }

    /// <summary>
    /// Returns the Marian target-language prefix piece (e.g. <c>&gt;&gt;pt&lt;&lt;</c>)
    /// for <paramref name="isoLanguageCode"/> if the model vocabulary contains it,
    /// or <see langword="null"/> if no prefix piece is defined. The returned piece is
    /// resolved to a token id at encode time by <see cref="EncodeSourceText"/>.
    /// </summary>
    public string? ResolveTargetLanguagePrefix(string isoLanguageCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoLanguageCode);
        string prefix = $">>{isoLanguageCode}<<";
        return modelIdByPiece.ContainsKey(prefix) ? prefix : null;
    }

    /// <summary>
    /// True when the model's vocabulary contains any <c>&gt;&gt;xx&lt;&lt;</c> language
    /// prefix piece, indicating it is a multi-target Marian model (e.g.
    /// <c>opus-mt-en-ROMANCE</c>) that needs a target-language prefix to disambiguate
    /// translations. False for single-pair models.
    /// </summary>
    public bool RequiresTargetLanguagePrefix { get; }

    public string DecodeTargetText(IEnumerable<long> tokenIds)
    {
        int[] targetTokenizerIds = tokenIds
            .Where(tokenId => tokenId != DecoderStartTokenId &&
                              tokenId != EndOfSentenceTokenId &&
                              tokenId != PadTokenId)
            .Select(MapModelIdToTargetTokenizerId)
            .ToArray();
        return targetTokenizer.Decode(targetTokenizerIds).Trim();
    }

    private int MapSourceTokenizerIdToModelId(int tokenizerId)
    {
        if (!sourcePieceByTokenizerId.TryGetValue(tokenizerId, out string? piece))
        {
            throw new InvalidOperationException($"Source tokenizer piece id '{tokenizerId}' did not resolve to a Marian token piece.");
        }

        if (modelIdByPiece.TryGetValue(piece, out int modelId))
        {
            return modelId;
        }

        if (modelIdByPiece.TryGetValue(sourceTokenizer.UnknownToken, out int unknownId))
        {
            return unknownId;
        }

        throw new InvalidOperationException($"Marian vocabulary did not define a model id for token piece '{piece}'.");
    }

    private int MapModelIdToTargetTokenizerId(long modelTokenId)
    {
        int checkedModelTokenId = checked((int)modelTokenId);
        if (!pieceByModelId.TryGetValue(checkedModelTokenId, out string? piece))
        {
            throw new InvalidOperationException($"Marian vocabulary did not define token piece for model id '{checkedModelTokenId}'.");
        }

        if (targetTokenizerIdByPiece.TryGetValue(piece, out int tokenizerId))
        {
            return tokenizerId;
        }

        if (targetTokenizerIdByPiece.TryGetValue(targetTokenizer.UnknownToken, out int unknownId))
        {
            return unknownId;
        }

        throw new InvalidOperationException($"Target tokenizer did not define an id for Marian token piece '{piece}'.");
    }

    private static async Task<OpusTokenizerConfig> LoadConfigAsync(string configPath, string generationConfigPath)
    {
        int decoderStartTokenId = 65000;
        int endOfSentenceTokenId = 0;
        int padTokenId = 65000;
        int maxGenerationLength = 256;

        if (File.Exists(configPath))
        {
            string configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(configText);
            JsonElement root = document.RootElement;
            decoderStartTokenId = ReadInt32(root, "decoder_start_token_id", decoderStartTokenId);
            endOfSentenceTokenId = ReadInt32(root, "eos_token_id", endOfSentenceTokenId);
            padTokenId = ReadInt32(root, "pad_token_id", padTokenId);
            maxGenerationLength = ReadInt32(root, "max_position_embeddings", maxGenerationLength);
        }

        if (File.Exists(generationConfigPath))
        {
            string genConfigText = await File.ReadAllTextAsync(generationConfigPath).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(genConfigText);
            JsonElement root = document.RootElement;
            maxGenerationLength = ReadInt32(root, "max_length", maxGenerationLength);
        }

        return new OpusTokenizerConfig(
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

    private static async Task<IReadOnlyDictionary<string, int>> LoadVocabularyAsync(string vocabPath)
    {
        string vocabText = await File.ReadAllTextAsync(vocabPath).ConfigureAwait(false);
        Dictionary<string, int>? vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabText);
        if (vocabulary is null || vocabulary.Count == 0)
        {
            throw new InvalidOperationException($"The Opus Marian vocabulary at '{vocabPath}' was empty or invalid.");
        }

        return vocabulary;
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
            $"The Opus tokenizer was not found under '{modelRootPath}'.",
            Path.Combine(modelRootPath, fileNames[0]));
    }

    private sealed record OpusTokenizerConfig(
        int DecoderStartTokenId,
        int EndOfSentenceTokenId,
        int PadTokenId,
        int MaxGenerationLength);
}
