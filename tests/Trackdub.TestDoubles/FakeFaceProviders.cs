using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.LipSynthesis;

namespace Trackdub.TestDoubles;

/// <summary>
/// Deterministic test double for <see cref="IFaceDetector"/>. No image/video I/O. Defaults to a
/// confident frontal face; toggle <see cref="FaceFound"/>/<see cref="Confidence"/> to drive the
/// no-face and low-confidence skip guards.
/// </summary>
public sealed class FakeFaceDetector : IFaceDetector
{
    public bool Available { get; set; } = true;
    public bool FaceFound { get; set; } = true;
    public double Confidence { get; set; } = 0.95;
    public FaceRegion Region { get; set; } = new(0.25, 0.25, 0.5, 0.5);
    public int CallCount { get; private set; }

    public bool IsAvailable => Available;

    public Task<FaceDetectionResult> DetectPrimaryFaceAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult(new FaceDetectionResult(
            FaceFound: FaceFound,
            Confidence: Confidence,
            PrimaryFace: FaceFound ? Region : null));
    }
}

/// <summary>
/// Deterministic test double for <see cref="IFaceLandmarkProvider"/>. Defaults to stable,
/// unoccluded landmarks; toggle to drive the unstable-crop and occlusion skip guards.
/// </summary>
public sealed class FakeFaceLandmarkProvider : IFaceLandmarkProvider
{
    public bool Available { get; set; } = true;
    public bool LandmarksFound { get; set; } = true;
    public bool IsStable { get; set; } = true;
    public bool MouthOccluded { get; set; }
    public int LandmarkCount { get; set; } = 68;
    public int CallCount { get; private set; }

    public bool IsAvailable => Available;

    public Task<FaceLandmarkResult> DetectLandmarksAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult(new FaceLandmarkResult(
            LandmarksFound: LandmarksFound,
            IsStable: IsStable,
            MouthOccluded: MouthOccluded,
            LandmarkCount: LandmarkCount));
    }
}

/// <summary>
/// Deterministic test double for <see cref="IFacePoseEstimator"/>. Defaults to a frontal pose;
/// set yaw/pitch beyond the option thresholds to drive the non-frontal skip guard.
/// </summary>
public sealed class FakeFacePoseEstimator : IFacePoseEstimator
{
    public bool Available { get; set; } = true;
    public double YawDegrees { get; set; }
    public double PitchDegrees { get; set; }
    public double RollDegrees { get; set; }
    public int CallCount { get; private set; }

    public bool IsAvailable => Available;

    public Task<FacePoseEstimate> EstimatePoseAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult(new FacePoseEstimate(YawDegrees, PitchDegrees, RollDegrees));
    }
}
