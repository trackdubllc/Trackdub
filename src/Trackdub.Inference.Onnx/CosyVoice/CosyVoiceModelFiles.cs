namespace Trackdub.Inference.Onnx.CosyVoice;

public sealed record CosyVoiceModelFiles(
    string ModelRootPath,
    string Variant,
    string CampPlusPath,
    string SpeechTokenizerPath,
    string TextEncoderPath,
    string TokenGeneratorPath,
    string FlowEncoderPath,
    string FlowDecoderEstimatorPath,
    string HiftF0PredictorPath,
    string HiftSourcePath,
    string HiftVocoderPath,
    string EmbeddingsDirectory,
    string TokenizerDirectory)
{
    public string PrimaryProbePath => TextEncoderPath;

    public static CosyVoiceModelFiles Resolve(string modelRootPath, string? variant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRootPath);
        string resolvedVariant = string.IsNullOrWhiteSpace(variant) ? "default" : variant;
        string root = Path.GetFullPath(modelRootPath);

        return new CosyVoiceModelFiles(
            root,
            resolvedVariant,
            Path.Combine(root, "campplus.onnx"),
            Path.Combine(root, "speech_tokenizer_v1.onnx"),
            ResolveVariantPath(root, resolvedVariant, ["llm", "text_encoder.onnx"], ["onnx_quantized_modelopt", "llm", "text_encoder.{0}.onnx"]),
            ResolveVariantPath(root, resolvedVariant, ["llm", "token_generator.onnx"], ["onnx_quantized_modelopt", "llm", "token_generator.{0}.onnx"]),
            ResolveVariantPath(root, resolvedVariant, ["flow", "encoder.onnx"], ["onnx_quantized_modelopt", "flow", "encoder.{0}.onnx"]),
            Path.Combine(root, "flow.decoder.estimator.fp32.onnx"),
            ResolveVariantPath(root, resolvedVariant, ["hift", "f0_predictor.onnx"], ["onnx_quantized_modelopt", "hift", "f0_predictor.{0}.onnx"]),
            ResolveVariantPath(root, resolvedVariant, ["hift", "source.onnx"], ["onnx_quantized_modelopt", "hift", "source.{0}.onnx"]),
            Path.Combine(root, "hift", "vocoder.onnx"),
            Path.Combine(root, "embeddings"),
            Path.Combine(root, "tokenizer"));
    }

    public IReadOnlyList<string> FindMissingFiles()
    {
        string[] required =
        [
            CampPlusPath,
            SpeechTokenizerPath,
            TextEncoderPath,
            TokenGeneratorPath,
            FlowEncoderPath,
            FlowDecoderEstimatorPath,
            HiftF0PredictorPath,
            HiftSourcePath,
            HiftVocoderPath,
            Path.Combine(HiftVocoderPath + ".data"),
            Path.Combine(EmbeddingsDirectory, "llm_speech_embedding.npy"),
            Path.Combine(EmbeddingsDirectory, "llm_llm_embedding.npy"),
            Path.Combine(EmbeddingsDirectory, "llm_spk_embed_affine_weight.npy"),
            Path.Combine(EmbeddingsDirectory, "llm_spk_embed_affine_bias.npy"),
            Path.Combine(EmbeddingsDirectory, "flow_input_embedding.npy"),
            Path.Combine(EmbeddingsDirectory, "flow_spk_embed_affine_weight.npy"),
            Path.Combine(EmbeddingsDirectory, "flow_spk_embed_affine_bias.npy"),
            Path.Combine(EmbeddingsDirectory, "flow_length_regulator.npz"),
            Path.Combine(TokenizerDirectory, "tiktoken_ranks.bin"),
            Path.Combine(TokenizerDirectory, "encode_smoke.json"),
            Path.Combine(ModelRootPath, "cosyvoice.yaml"),
            Path.Combine(ModelRootPath, "config.json"),
            Path.Combine(ModelRootPath, "configuration.json"),
        ];

        return required
            .Where(path => !File.Exists(path))
            .Select(path => Path.GetRelativePath(ModelRootPath, path).Replace('\\', '/'))
            .ToArray();
    }

    private static string ResolveVariantPath(
        string root,
        string variant,
        string[] defaultRelativeParts,
        string[] variantRelativeParts)
    {
        string defaultPath = Path.Combine([root, .. defaultRelativeParts]);
        if (variant.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return defaultPath;
        }

        string[] variantParts = variantRelativeParts
            .Select(part => part.Contains("{0}", StringComparison.Ordinal) ? string.Format(part, variant) : part)
            .ToArray();
        string variantPath = Path.Combine([root, .. variantParts]);
        return File.Exists(variantPath) ? variantPath : defaultPath;
    }
}
