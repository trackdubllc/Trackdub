using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

internal static class StageWorkloadProfileCatalog
{
    public static IReadOnlyDictionary<RuntimeStage, StageWorkloadProfile> All { get; } =
        new Dictionary<RuntimeStage, StageWorkloadProfile>
        {
            [RuntimeStage.Vad] = new(RuntimeStage.Vad, ModelSizeMb: 2, LatencySensitivity.High, PeakMemoryMb: 20),
            [RuntimeStage.Asr] = new(RuntimeStage.Asr, ModelSizeMb: 75, LatencySensitivity.Medium, PeakMemoryMb: 300),
            [RuntimeStage.Translation] = new(RuntimeStage.Translation, ModelSizeMb: 40, LatencySensitivity.High, PeakMemoryMb: 200),
            [RuntimeStage.Tts] = new(RuntimeStage.Tts, ModelSizeMb: 82, LatencySensitivity.Medium, PeakMemoryMb: 400),
            [RuntimeStage.Diarization] = new(RuntimeStage.Diarization, ModelSizeMb: 90, LatencySensitivity.Low, PeakMemoryMb: 350),
            [RuntimeStage.Separation] = new(RuntimeStage.Separation, ModelSizeMb: 150, LatencySensitivity.Low, PeakMemoryMb: 600),
            [RuntimeStage.SpeechEnhancement] = new(RuntimeStage.SpeechEnhancement, ModelSizeMb: 8, LatencySensitivity.Low, PeakMemoryMb: 200),
            [RuntimeStage.LipSync] = new(RuntimeStage.LipSync, ModelSizeMb: 90, LatencySensitivity.Low, PeakMemoryMb: 300),
            [RuntimeStage.TextRefinement] = new(RuntimeStage.TextRefinement, ModelSizeMb: 40, LatencySensitivity.High, PeakMemoryMb: 250),
            [RuntimeStage.OverlapRescue] = new(RuntimeStage.OverlapRescue, ModelSizeMb: 150, LatencySensitivity.Low, PeakMemoryMb: 600),
            // M23 video lip synthesis. The real provider is an experimental Python/CUDA engine
            // OUTSIDE ONNX execution-provider planning, so it is intentionally absent from
            // StageRuntimeRequirementsCatalog. This entry exists only to satisfy the
            // catalog-completeness invariant (every RuntimeStage needs a profile); the values are
            // clamped placeholders — the catalog range caps PeakMemoryMb at 3000 MB and cannot
            // express the real ~12 GB VRAM target, which is gated/benchmarked separately.
            [RuntimeStage.LipSynthesis] = new(RuntimeStage.LipSynthesis, ModelSizeMb: 1000, LatencySensitivity.Low, PeakMemoryMb: 3000),
        };
}
