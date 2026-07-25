namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Intermediate progress report emitted within a single pipeline stage.
/// Consumed by StageProgressAdapter (Sdk) to produce PipelineProgressEvent(kind=Progress).
/// </summary>
public sealed record StageProgressReport(
    /// <summary>Stage name (StageNames.* constant).</summary>
    string StageName,

    /// <summary>0–100 percentage. Null for activity-only stages (VAD, Diarization, Separation).</summary>
    double? PercentComplete,

    /// <summary>Items processed so far (segments, regions, chunks).</summary>
    int ItemsComplete,

    /// <summary>Total items. Null when total is not known upfront.</summary>
    int? TotalItems,

    /// <summary>Best-effort remaining time. Null when insufficient history.</summary>
    TimeSpan? EstimatedTimeRemaining,

    /// <summary>Human-readable label for display. E.g. "12 / 38 segments".</summary>
    string? DisplayLabel);
