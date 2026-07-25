using Trackdub.Domain.LipSynthesis;

namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Shared input for the M23 face-analysis seams. Identifies the speaker turn (a time window in
/// the original video) to analyze before any synthesis is attempted.
/// </summary>
public sealed record FaceAnalysisRequest(
    string VideoPath,
    TimeSpan Start,
    TimeSpan End,
    string? SpeakerId);

/// <summary>
/// Detects the primary face within a speaker turn. Availability is a distinct state from
/// detection success: a registered detector with no model installed reports IsAvailable=false
/// and never fakes a detection.
/// </summary>
public interface IFaceDetector
{
    bool IsAvailable { get; }

    Task<FaceDetectionResult> DetectPrimaryFaceAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken);
}

public sealed record FaceDetectionResult(
    bool FaceFound,
    double Confidence,
    FaceRegion? PrimaryFace);

/// <summary>Provides facial landmarks and basic stability/occlusion signals for a turn.</summary>
public interface IFaceLandmarkProvider
{
    bool IsAvailable { get; }

    Task<FaceLandmarkResult> DetectLandmarksAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken);
}

public sealed record FaceLandmarkResult(
    bool LandmarksFound,
    bool IsStable,
    bool MouthOccluded,
    int LandmarkCount,
    IReadOnlyList<(float X, float Y)>? LandmarkPoints = null);

/// <summary>Estimates head pose so non-frontal turns can be skipped rather than hallucinated.</summary>
public interface IFacePoseEstimator
{
    bool IsAvailable { get; }

    Task<FacePoseEstimate> EstimatePoseAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken);
}

public sealed record FacePoseEstimate(
    double YawDegrees,
    double PitchDegrees,
    double RollDegrees);
