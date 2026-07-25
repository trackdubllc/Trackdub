namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// Records a single stage failure observed during the current session.
/// </summary>
public sealed record StageFailureRecord(
    string StageName,
    FailureCategory Category,
    string? Reason);

/// <summary>
/// A point-in-time snapshot of the application health for the current session.
/// </summary>
public sealed record AppHealthSummary(
    IReadOnlyList<string> CompletedStages,
    IReadOnlyList<StageFailureRecord> FailedStages)
{
    /// <summary>
    /// Returns <see langword="true"/> when no stage failures have been recorded in this session.
    /// </summary>
    public bool IsHealthy => FailedStages.Count == 0;
}
