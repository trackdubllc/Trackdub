namespace Trackdub.Domain.LipSynthesis;

/// <summary>
/// Per-speaker-turn outcome of M23 video lip synthesis (mouth repair on original footage).
/// One record per processed turn. Mirrors the LipSync segment shape but tracks video/face
/// concerns and an explicit experimental-provider flag for honest UI labeling.
/// </summary>
public sealed record LipSynthesisSegment(
    Guid SegmentId,
    LipSynthesisSegmentStatus Status,
    string? SpeakerId,
    TimeSpan TurnStart,
    TimeSpan TurnEnd,
    double? FaceConfidence,
    /// <summary>Relative path to the patched video clip for this turn, when synthesized.</summary>
    string? PatchedClipRelativePath,
    string? SkipReason,
    string? FailureReason,
    string? ProviderId,
    string? ModelId,
    /// <summary>True when an experimental (non-default-lane) provider produced this turn.</summary>
    bool UsedExperimentalProvider,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Rich per-turn status. Skipped* variants are quality/availability guards that preserve the
/// original frames; Failed is an actual synthesis error. NotRun means the stage never executed
/// for this turn.
/// </summary>
public enum LipSynthesisSegmentStatus
{
    NotRun = 0,
    Synthesized = 1,
    SkippedNoFace = 10,
    SkippedNonFrontal = 11,
    SkippedLowConfidence = 12,
    SkippedOccluded = 13,
    SkippedUnstableCrop = 14,
    SkippedLicenseGate = 15,
    SkippedRuntimeUnavailable = 16,
    SkippedExperimentalGate = 17,
    Failed = 20
}

/// <summary>Normalized face/mouth region within a video frame (provider-neutral value object).</summary>
public sealed record FaceRegion(double X, double Y, double Width, double Height);

/// <summary>
/// Plan for cropping a single speaker turn's face region for synthesis. The crop is per-turn so
/// GPU memory pressure is bounded and skipped turns can preserve original frames.
/// </summary>
public sealed record SpeakerTurnCropPlan(
    Guid SegmentId,
    string? SpeakerId,
    TimeSpan TurnStart,
    TimeSpan TurnEnd,
    FaceRegion Crop,
    bool IsStable);

/// <summary>
/// Plan describing how patched per-turn clips are recomposited over the original video. Turns not
/// listed here retain their original frames — the source video is always the authority.
/// </summary>
public sealed record VideoRecompositionPlan(
    string SourceVideoRelativePath,
    IReadOnlyList<RecomposedTurn> PatchedTurns);

public sealed record RecomposedTurn(
    Guid SegmentId,
    TimeSpan Start,
    TimeSpan End,
    string PatchedClipRelativePath);
