namespace Trackdub.Domain.LipSync;

public sealed record LipSyncSegment(
    Guid SegmentId,
    LipSyncSegmentStatus Status,
    string? SourceAlignmentId,
    string? TtsAlignmentId,
    TimeSpan SourceDuration,
    TimeSpan TtsDuration,
    TimeSpan? AlignedTtsDuration,
    double? PlanConfidence,
    string? SkipReason,
    string? FailureReason,
    string? ProviderId,
    string? ModelId,
    DateTimeOffset CreatedAtUtc);

public enum LipSyncSegmentStatus
{
    NotRun = 0,
    Aligned = 1,
    Partial = 2,
    SkippedLowConfidence = 10,
    SkippedNoPhonemes = 11,
    SkippedInventoryMismatch = 12,
    SkippedUnsafeStretchRatio = 13,
    SkippedLicenseGate = 14,
    SkippedRuntimeUnavailable = 15,
    Failed = 20
}


