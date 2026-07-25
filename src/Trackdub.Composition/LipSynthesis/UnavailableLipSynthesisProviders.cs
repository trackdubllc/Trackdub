using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.LipSynthesis;

namespace Trackdub.Composition.LipSynthesis;

// Fallback stubs for the M23 video lip-synthesis seams. Used only when no real provider is
// registered. Stubs report IsAvailable=false so the stage skips cleanly (SkippedRuntimeUnavailable)
// and audio-only export is never blocked. This is honest readiness — not a fake engine.

/// <summary>Default M23 engine: never available until a real provider is wired and gated.</summary>
public sealed class UnavailableLipSynthesisEngine : ILipSynthesisEngine
{
    public bool IsAvailable => false;
    public bool IsExperimental => true;
    public string ProviderId => "none";
    public string ModelId => "none";

    public Task<LipSynthesisResult> SynthesizeTurnAsync(
        LipSynthesisRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new LipSynthesisResult(
            SegmentId: request.SegmentId,
            Status: LipSynthesisEngineStatus.Skipped,
            PatchedClipPath: null,
            SkipReason: "No video lip-synthesis provider is installed and verified.",
            FailureReason: null,
            ProviderId: "none",
            ModelId: "none"));
}

public sealed class UnavailableFaceDetector : IFaceDetector
{
    public bool IsAvailable => false;

    public Task<FaceDetectionResult> DetectPrimaryFaceAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new FaceDetectionResult(FaceFound: false, Confidence: 0d, PrimaryFace: null));
}

public sealed class UnavailableFaceLandmarkProvider : IFaceLandmarkProvider
{
    public bool IsAvailable => false;

    public Task<FaceLandmarkResult> DetectLandmarksAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new FaceLandmarkResult(LandmarksFound: false, IsStable: false, MouthOccluded: false, LandmarkCount: 0));
}

public sealed class UnavailableFacePoseEstimator : IFacePoseEstimator
{
    public bool IsAvailable => false;

    public Task<FacePoseEstimate> EstimatePoseAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new FacePoseEstimate(YawDegrees: 0d, PitchDegrees: 0d, RollDegrees: 0d));
}
