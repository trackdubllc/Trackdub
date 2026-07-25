namespace Trackdub.Inference.Onnx.Qwen3Tts.Pipeline;

/// <summary>
/// Configuration options for constructing a <see cref="TtsPipeline"/> or <see cref="QwenTextToSpeechClient"/>.
/// </summary>
/// <remarks>
/// <see cref="ModelVariant"/> controls instruct gating and expected talker width. CustomVoice preset
/// synthesis uses <see cref="TtsPipeline"/>; Base bundles with <c>speaker_encoder.onnx</c> use
/// <see cref="VoiceClonePipeline"/> instead (see <see cref="Qwen3Tts.Qwen3TtsEngine"/>).
/// </remarks>
public class QwenTtsOptions
{
    /// <summary>
    /// Path to local model directory. When null, uses the variant-specific default shared location.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// HuggingFace repository ID for model download.
    /// When null, automatically determined from <see cref="ModelVariant"/>.
    /// </summary>
    public string? HuggingFaceRepo { get; set; }

    /// <summary>
    /// Model size variant. Defaults to <see cref="QwenModelVariant.Qwen06B"/>.
    /// </summary>
    public QwenModelVariant ModelVariant { get; set; } = QwenModelVariant.Qwen06B;

    /// <summary>
    /// Default instruction text for speech style control (e.g., "Read with a calm, warm tone").
    /// Only effective when <see cref="ModelVariant"/> is <see cref="QwenModelVariant.Qwen17B"/> or higher.
    /// Ignored for 0.6B models which do not support instruction control.
    /// </summary>
    public string? InstructText { get; set; }

    /// <summary>
    /// Optional custom session options factory for ONNX Runtime sessions.
    /// </summary>
    public Func<Microsoft.ML.OnnxRuntime.SessionOptions>? SessionOptionsFactory { get; set; }
}
