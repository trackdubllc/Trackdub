using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Routing;

public interface IInferenceEngineAdapter
{
    string EngineFamily { get; }
}

public interface ISpeechRegionDetectorAdapter : IInferenceEngineAdapter, ISpeechRegionDetector
{
    Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        SpeechRegionDetectionRequest request,
        StageRuntimePlan plan,
        CancellationToken cancellationToken);
}

public interface IAudioTranscriptionEngineAdapter : IInferenceEngineAdapter, IAudioTranscriptionEngine
{
    Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        AudioTranscriptionRequest request,
        StageRuntimePlan plan,
        CancellationToken cancellationToken);
}

public interface ISpeakerDiarizationEngineAdapter : IInferenceEngineAdapter, ISpeakerDiarizationEngine
{
    Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
        SpeakerDiarizationRequest request,
        StageRuntimePlan plan,
        CancellationToken cancellationToken);
}

public interface IStemSeparationEngineAdapter : IInferenceEngineAdapter, IStemSeparationEngine
{
    Task<StemSeparationResult> SeparateAsync(
        StemSeparationRequest request,
        StageRuntimePlan plan,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IOverlapRescueEngineAdapter : IInferenceEngineAdapter, IOverlapRescueEngine
{
    Task<OverlapRescueResult> RescueAsync(
        OverlapRescueRequest request,
        StageRuntimePlan plan,
        IProgress<OverlapRescueProgress>? progress,
        CancellationToken cancellationToken);
}

public interface ITranslationEngineAdapter : IInferenceEngineAdapter, ITranslationEngine
{
}

public interface ITtsEngineAdapter : IInferenceEngineAdapter, ITtsEngine
{
    Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        StageRuntimePlan plan,
        CancellationToken cancellationToken);
}
