namespace Trackdub.Composition.StarterPacks;

/// <summary>
/// Bundled starter-pack models to exercise with <c>Trackdub.Benchmarks --scope trt-rtx-smoke</c>
/// and optional TRT RTX integration smoke tests.
/// Skips cleanly when a model is not cached locally.
/// </summary>
public static class TrtRtxSmokeCatalog
{
    public const string ScopeName = "trt-rtx-smoke";

    public sealed record Target(string ModelReference, string? Variant, string Label);

    /// <summary>
    /// Starter-pack GPU models where TRT RTX smoke is worth attempting.
    /// Silero VAD is omitted (planner excludes TensorRT families).
    /// Qwen ASR is included; planner keeps TensorRT RTX only if encoder, decoder_init, and decoder_step smoke all pass.
    /// </summary>
    public static IReadOnlyList<Target> StarterPackTurboGpu { get; } =
    [
        new("cgus/diar_streaming_sortformer_4spk-v2.1-onnx", null, "diarization"),
        new("openai/whisper-small", null, "asr-whisper-small"),
        new("openai/whisper-medium", null, "asr-whisper-medium"),
        new("tonythethompson/qwen3-asr-0.6b-onnx", null, "asr-qwen-0.6b"),
        new("tonythethompson/qwen3-asr-1.7b-onnx", null, "asr-qwen-1.7b"),
        new("microsoft/Phi-4-mini-instruct-onnx", "gpu-int4", "translation-phi"),
        new("google/madlad400-3b-mt", "quantized", "translation-madlad"),
        new("ResembleAI/chatterbox-turbo-ONNX", "fp16", "tts-chatterbox"),
    ];
}
