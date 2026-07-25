namespace Trackdub.Domain.Artifacts;

public sealed record PipelineDegradationRecord(
    string Stage,
    string Code,
    string Message,
    string? Detail,
    string? SelectedFallback,
    string? RecommendedAction,
    DateTimeOffset OccurredAtUtc,
    Guid? StageRunId);
