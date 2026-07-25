using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Qwen3Tts;

internal sealed class Qwen3TtsModelFiles
{
    private Qwen3TtsModelFiles(string rootDirectory, bool isBaseModel, bool isLargeModel, string? modelAlias)
    {
        RootDirectory = rootDirectory;
        IsBaseModel = isBaseModel;
        IsLargeModel = isLargeModel;
        ModelAlias = modelAlias;
    }

    public string RootDirectory { get; }

    public bool IsBaseModel { get; }

    public bool IsLargeModel { get; }

    public string? ModelAlias { get; }

    public string TokenizerDirectory => Path.Combine(RootDirectory, "tokenizer");

    public string EmbeddingsDirectory => Path.Combine(RootDirectory, "embeddings");

    public string SpeakerEncoderPath => Path.Combine(RootDirectory, "speaker_encoder.onnx");

    public string VocoderPath => Path.Combine(RootDirectory, "vocoder.onnx");

    public static Qwen3TtsModelFiles Resolve(
        BenchmarkModelCandidate candidate,
        StageRuntimePlan plan)
    {
        string rootDirectory = !string.IsNullOrWhiteSpace(plan.ModelRootPath)
            ? Path.GetFullPath(plan.ModelRootPath)
            : candidate.RootDirectory
                ?? Path.GetDirectoryName(candidate.ModelPath)
                ?? throw new InvalidOperationException("Cannot resolve Qwen3-TTS model root path.");

        string? alias = plan.ModelAlias;
        bool isBaseModel = IsBaseAlias(alias) || File.Exists(Path.Combine(rootDirectory, "speaker_encoder.onnx"));
        bool isLargeModel = IsLargeAlias(alias);

        if (File.Exists(SpeakerEncoderPathFor(rootDirectory)) == false && isBaseModel)
        {
            throw new FileNotFoundException(
                "Qwen3-TTS Base voice cloning requires speaker_encoder.onnx in the model root.",
                Path.Combine(rootDirectory, "speaker_encoder.onnx"));
        }

        foreach (string requiredPath in RequiredSharedPaths(rootDirectory))
        {
            if (!File.Exists(requiredPath))
            {
                throw new FileNotFoundException("Qwen3-TTS bundle is missing a required file.", requiredPath);
            }
        }

        return new Qwen3TtsModelFiles(rootDirectory, isBaseModel, isLargeModel, alias);
    }

    private static string SpeakerEncoderPathFor(string rootDirectory) =>
        Path.Combine(rootDirectory, "speaker_encoder.onnx");

    private static IEnumerable<string> RequiredSharedPaths(string rootDirectory)
    {
        yield return Path.Combine(rootDirectory, "talker_prefill.onnx");
        yield return Path.Combine(rootDirectory, "talker_decode.onnx");
        yield return Path.Combine(rootDirectory, "code_predictor.onnx");
        yield return Path.Combine(rootDirectory, "vocoder.onnx");
        yield return Path.Combine(rootDirectory, "tokenizer", "vocab.json");
        yield return Path.Combine(rootDirectory, "tokenizer", "merges.txt");
        yield return Path.Combine(rootDirectory, "embeddings", "config.json");
        yield return Path.Combine(rootDirectory, "embeddings", "speaker_ids.json");
    }

    private static bool IsBaseAlias(string? alias) =>
        !string.IsNullOrWhiteSpace(alias)
        && alias.Contains("base", StringComparison.OrdinalIgnoreCase);

    private static bool IsLargeAlias(string? alias) =>
        !string.IsNullOrWhiteSpace(alias)
        && alias.Contains("1.7b", StringComparison.OrdinalIgnoreCase);
}
