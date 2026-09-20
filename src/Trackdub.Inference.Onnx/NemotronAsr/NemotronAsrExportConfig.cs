using System.Text.Json;

namespace Trackdub.Inference.Onnx.NemotronAsr;

/// <summary>
/// Values the bundled Nemotron ASR export actually trains/exports with.
/// These must come from config.json — deriving blank id from tokenizer size
/// is off-by-one on this package (blank_id == vocab_size, not vocab_size-1).
/// </summary>
internal sealed record NemotronAsrExportConfig(
    int VocabSize,
    int BlankId,
    string Normalize,
    bool HasPromptInput)
{
    /// <summary>NeMo "NA" / empty = no per-feature mel normalization.</summary>
    public bool ApplyPerFeatureNormalization =>
        !string.IsNullOrWhiteSpace(Normalize) &&
        !Normalize.Equals("NA", StringComparison.OrdinalIgnoreCase) &&
        !Normalize.Equals("none", StringComparison.OrdinalIgnoreCase) &&
        !Normalize.Equals("null", StringComparison.OrdinalIgnoreCase);

    /// <summary>RNNT predictor seed: blank, not a real SentencePiece piece.</summary>
    public int DecoderStartTokenId => BlankId;

    public static NemotronAsrExportConfig Load(string configPath, bool hasPromptInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement root = document.RootElement;

        int vocabSize = ReadPositiveInt(root, "vocab_size") ?? 0;
        int blankId = ReadInt(root, "blank_id") ?? -1;
        if (blankId < 0)
        {
            // This export family places blank at vocab_size (joint emits vocab_size+1 logits).
            blankId = vocabSize > 0 ? vocabSize : 0;
        }

        string normalize = "NA";
        if (root.TryGetProperty("preprocessor", out JsonElement preprocessor) &&
            preprocessor.ValueKind == JsonValueKind.Object &&
            preprocessor.TryGetProperty("normalize", out JsonElement normalizeElement) &&
            normalizeElement.ValueKind == JsonValueKind.String)
        {
            normalize = normalizeElement.GetString() ?? "NA";
        }

        return new NemotronAsrExportConfig(vocabSize, blankId, normalize, hasPromptInput);
    }

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out int value)
            ? value
            : null;

    private static int? ReadPositiveInt(JsonElement root, string name)
    {
        int? value = ReadInt(root, name);
        return value is > 0 ? value : null;
    }
}
