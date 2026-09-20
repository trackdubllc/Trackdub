using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Runtime.Planning;

public sealed record StageRuntimeRequirements(
    RuntimeStage Stage,
    ModelTask RequiredTask,
    IReadOnlyList<string> PreferredModelAliases,
    IReadOnlyList<ExecutionProviderKind> AllowedProvidersThisMilestone,
    IReadOnlyList<string> PreferredGpuVariants,
    IReadOnlyList<string> PreferredCpuVariants,
    IReadOnlyDictionary<string, IReadOnlyList<ExecutionProviderKind>>? AllowedProvidersByEngineFamily = null,
    IReadOnlyList<string>? AllowedEngineFamilies = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    bool PreferTopRankedModelUntilReady = false);

internal static class Milestone5PlanningPolicy
{
    public static IReadOnlyList<ExecutionProviderKind> SupportedProvidersThisMilestone { get; } =
    [
        ExecutionProviderKind.TensorRTRtx,
        ExecutionProviderKind.Migraphx,
        ExecutionProviderKind.OpenVinoCatalog,
        ExecutionProviderKind.Qnn,
        ExecutionProviderKind.VitisAi,
        ExecutionProviderKind.TensorRt,
        ExecutionProviderKind.Cuda,
        ExecutionProviderKind.OpenVino,
        ExecutionProviderKind.CoreMl,
        ExecutionProviderKind.DirectMl,
        ExecutionProviderKind.Dnnl,
        ExecutionProviderKind.Cpu
    ];
}

internal static class StageRuntimeRequirementsCatalog
{
    private static IReadOnlyList<ExecutionProviderKind> DefaultOnnxStageAllowedProviders =>
        Milestone5PlanningPolicy.SupportedProvidersThisMilestone;

    private static IReadOnlyList<ExecutionProviderKind> WithoutTensorRtFamilies(
        IReadOnlyList<ExecutionProviderKind> providers) =>
        [.. providers.Where(static p => p is not ExecutionProviderKind.TensorRTRtx and not ExecutionProviderKind.TensorRt)];

    public static IReadOnlyDictionary<RuntimeStage, StageRuntimeRequirements> All { get; } =
        new Dictionary<RuntimeStage, StageRuntimeRequirements>
        {
            [RuntimeStage.Vad] = new(
                RuntimeStage.Vad,
                ModelTask.Vad,
                ["silero-vad", "silero"],
                // TensorRT RTX is allowed again for VAD; the per-model smoke test gates it
                // (silero-vad previously passed a TRT RTX smoke run) and DirectML remains the fallback.
                DefaultOnnxStageAllowedProviders,
                ["fp16", "q4f16"],
                ["int8", "quantized", "uint8", "q4"]),
            [RuntimeStage.Asr] = new(
                RuntimeStage.Asr,
                ModelTask.Asr,
                [
                    "qwen3-asr-0.6b",
                    "qwen3-asr-balanced",
                    "qwen3-asr-1.7b",
                    "qwen3-asr-quality",
                    "whisper-tiny-onnx",
                    "whisper-tiny",
                    "whisper-tiny-local",
                    "whisper-tiny-genai",
                ],
                DefaultOnnxStageAllowedProviders,
                ["default", "fp16"],
                ["default", "int8", "quantized", "uint8", "q4"],
                // Stock Olive whisper-onnx graphs omit trt-rtx in supported_providers and use
                // fused contrib ops TensorRT RTX cannot import. Keep qwen3-asr on TRT RTX.
                // whisper-genai loads through ORT GenAI, whose NvTensorRtRtx device can
                // terminate the process (native stack overflow) during model init/generation.
                new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["whisper-onnx"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                    ["whisper-genai"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                },
                // Nemotron ASR is not in the shipping auto-planning lane: quality-tier ranking
                // previously selected it ahead of working ONNX models and produced empty
                // transcripts. Explicit Nemotron override still resolves by alias if needed.
                AllowedEngineFamilies: ["qwen3-asr", "whisper-onnx", "whisper-genai"]),
            [RuntimeStage.Translation] = new(
                RuntimeStage.Translation,
                ModelTask.Translation,
                ["opus-en-es", "helsinki-opus-en-es", "opus-en-fr", "opus-en-de", "opus-en-it", "opus-en-pt", "opus-es-en", "helsinki-opus-es-en", "madlad400-mt", "madlad400"],
                DefaultOnnxStageAllowedProviders,
                ["merged-decoder", "quantized", "fp16"],
                ["merged-decoder", "quantized", "int8", "fp16"],
                // Encoder-decoder InferenceSession ctor stack-overflows under TensorRT RTX (ORT 1.24.5).
                // phi-genai loads through ORT GenAI, whose NvTensorRtRtx device can terminate
                // the process (native stack overflow) during model init/generation.
                new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["opus-mt"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                    ["madlad"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                    ["phi-genai"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                }),
            [RuntimeStage.Diarization] = new(
                RuntimeStage.Diarization,
                ModelTask.Diarization,
                ["diar-streaming-sortformer-4spk-v2.1", "sortformer-diarizer-4spk-v2.1", "sortformer-4spk", "nvidia-streaming-sortformer-4spk-v2.1"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["default"]),
            [RuntimeStage.Separation] = new(
                RuntimeStage.Separation,
                ModelTask.Separation,
                ["spleeter"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["default"],
                AllowedEngineFamilies: ["spleeter"],
                PreferTopRankedModelUntilReady: true),
            [RuntimeStage.OverlapRescue] = new(
                RuntimeStage.OverlapRescue,
                ModelTask.OverlapRescue,
                ["sepformer"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["default"],
                AllowedEngineFamilies: ["sepformer"]),
            [RuntimeStage.SpeechEnhancement] = new(
                RuntimeStage.SpeechEnhancement,
                ModelTask.SpeechEnhancement,
                ["deepfilternet3", "deepfilter"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["default"],
                AllowedEngineFamilies: ["deepfilternet3"]),
            [RuntimeStage.Tts] = new(
                RuntimeStage.Tts,
                ModelTask.Tts,
                ["kokoro-onnx", "kokoro", "chatterbox-turbo-onnx", "chatterbox-turbo", "chatterbox-onnx", "chatterbox",
                    "cosyvoice-300m", "cosyvoice",
                    "qwen3-tts-0.6b-customvoice", "qwen3-tts-0.6b", "qwen3-tts", "qwen-tts",
                    "qwen3-tts-1.7b-customvoice", "qwen3-tts-1.7b",
                    "qwen3-tts-0.6b-base", "qwen3-tts-1.7b-base"],
                DefaultOnnxStageAllowedProviders,
                ["int8", "q4f16", "fp16", "q4", "default", "int4"],
                ["int8", "q4", "quantized", "default"],
                // Chatterbox / CosyVoice / Qwen3-TTS ONNX graphs hit TensorRT RTX unsupported
                // ops (e.g. Squeeze) and hard-fail session init under a global trt-rtx pin.
                // Prefer DirectML/CPU for those families; keep TRT only for smoke-proven routes.
                new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kokoro"] = [ExecutionProviderKind.Cpu],
                    ["chatterbox"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                    ["cosyvoice"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                    ["qwen3-tts"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                },
                AllowedEngineFamilies: ["kokoro", "chatterbox", "cosyvoice", "qwen3-tts"]),
            [RuntimeStage.LipSync] = new(
                RuntimeStage.LipSync,
                ModelTask.ForcedAlignment,
                ["wav2vec2-lv60-espeak", "wav2vec2-lv60-espeak-cv-ft-onnx"],
                DefaultOnnxStageAllowedProviders,
                ["default"],
                ["performance", "default"],
                AllowedEngineFamilies: ["onnx-ctc-phoneme-aligner"],
                PreferTopRankedModelUntilReady: true),
            [RuntimeStage.TextRefinement] = new(
                RuntimeStage.TextRefinement,
                ModelTask.TextRefinement,
                ["qwen2.5-1.5b-instruct", "qwen-polisher", "text-refiner"],
                DefaultOnnxStageAllowedProviders,
                ["default", "fp16"],
                ["default", "int8", "quantized"],
                // ORT GenAI NvTensorRtRtx terminates the process (native stack overflow) when
                // loading/running bundled GenAI models, e.g. qwen-instruct (Qwen2.5-1.5B). A
                // smoke failure cannot gate a fatal crash, so GenAI families never see TRT RTX.
                new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["qwen-instruct"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                    ["phi-genai"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                }),
            [RuntimeStage.LipSynthesis] = new(
                RuntimeStage.LipSynthesis,
                ModelTask.LipSynthesis,
                ["latentsync-1.6", "latentsync"],
                DefaultOnnxStageAllowedProviders,
                ["fp16", "default"],
                ["int8", "default"],
                // LatentSync UNet exports MultiHeadAttention, which TensorRT RTX cannot import.
                AllowedProvidersByEngineFamily: new Dictionary<string, IReadOnlyList<ExecutionProviderKind>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["latentsync-diffusion"] = WithoutTensorRtFamilies(DefaultOnnxStageAllowedProviders),
                },
                AllowedEngineFamilies: ["latentsync-diffusion"]),
        };
}
