namespace Trackdub.Inference.Onnx.Qwen3Tts.Pipeline;

/// <summary>
/// Identifies the Qwen3-TTS talker model size bundled for inference.
/// </summary>
/// <remarks>
/// <para>
/// Trackdub resolves ONNX roots from <c>bundled-models.manifest.json</c>; this enum selects
/// variant-specific behavior (hidden size expectations, instruct support, cache subdirectory names).
/// Runtime tensor shapes always come from the bundle's <c>embeddings/config.json</c>, not from
/// these constants alone.
/// </para>
/// <para>
/// <b>Audio codec (RVQ):</b> both variants emit 16 residual-vector-quantized codebook indices
/// at 12&nbsp;Hz. The vocoder upsamples those frames to 24&nbsp;kHz mono PCM. This is distinct
/// from ONNX weight quantization; shipped ElBruno bundles use full-precision ONNX graphs unless
/// a future manifest entry documents otherwise.
/// </para>
/// </remarks>
public enum QwenModelVariant
{
    /// <summary>
    /// 0.6B-parameter CustomVoice talker. Talker hidden size 1024; no instruction-control path.
    /// </summary>
    Qwen06B = 0,

    /// <summary>
    /// 1.7B-parameter CustomVoice talker. Talker hidden size 2048; supports optional instruct text.
    /// </summary>
    Qwen17B = 1,
}

/// <summary>
/// Static metadata for <see cref="QwenModelVariant"/> selection and feature gates.
/// </summary>
/// <remarks>
/// Use these helpers when wiring download/bootstrap tooling or validating manifest aliases.
/// Inference code should prefer dimensions loaded from <see cref="Models.EmbeddingStore"/> rather
/// than hard-coding sizes from this class.
/// </remarks>
public static class QwenModelVariantConfig
{
    /// <summary>Default variant when none is specified.</summary>
    public const QwenModelVariant Default = QwenModelVariant.Qwen06B;

    /// <summary>Returns the expected Talker LM hidden size for a variant.</summary>
    public static int GetHiddenSize(QwenModelVariant variant) => variant switch
    {
        QwenModelVariant.Qwen06B => 1024,
        QwenModelVariant.Qwen17B => 2048,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown model variant")
    };

    /// <summary>Returns the expected Talker LM intermediate (MLP) size for a variant.</summary>
    public static int GetIntermediateSize(QwenModelVariant variant) => variant switch
    {
        QwenModelVariant.Qwen06B => 3072,
        QwenModelVariant.Qwen17B => 6144,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown model variant")
    };

    /// <summary>Returns the HuggingFace repository ID for a variant.</summary>
    public static string GetRepoId(QwenModelVariant variant) => variant switch
    {
        QwenModelVariant.Qwen06B => "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
        QwenModelVariant.Qwen17B => "elbruno/Qwen3-TTS-12Hz-1.7B-CustomVoice-ONNX",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown model variant")
    };

    /// <summary>
    /// Returns the model subdirectory name for a variant.
    /// Used to keep different model files separate under the shared cache root.
    /// </summary>
    public static string GetModelSubDir(QwenModelVariant variant) => variant switch
    {
        QwenModelVariant.Qwen06B => "0.6B",
        QwenModelVariant.Qwen17B => "1.7B",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown model variant")
    };

    /// <summary>
    /// Trackdub supplies ONNX roots via bundled manifest entries; ElBruno downloader defaults are not used.
    /// </summary>
    public static string GetDefaultModelDir(QwenModelVariant variant) =>
        throw new NotSupportedException(
            "Qwen3-TTS model directories are resolved from bundled manifest inventory, not ElBruno download defaults.");

    /// <summary>Returns whether a variant supports instruction control (emotion, rate, timbre).</summary>
    public static bool SupportsInstruct(QwenModelVariant variant) => variant switch
    {
        QwenModelVariant.Qwen06B => false,
        QwenModelVariant.Qwen17B => true,
        _ => false
    };

    /// <summary>Returns all defined model variants.</summary>
    public static QwenModelVariant[] GetAllVariants() =>
        [QwenModelVariant.Qwen06B, QwenModelVariant.Qwen17B];
}
