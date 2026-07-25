namespace Trackdub.Contracts.Dubbing;

/// <summary>
/// Status of an individual pipeline stage execution.
/// </summary>
public enum StageStatus
{
    /// <summary>The stage completed successfully.</summary>
    Succeeded,

    /// <summary>The stage was skipped due to missing prerequisites or configuration.</summary>
    Skipped,

    /// <summary>The stage failed with an unrecoverable error.</summary>
    Failed,
}

/// <summary>
/// Immutable outcome record for a single pipeline stage execution,
/// capturing timing, artifacts, degradation records, and reason codes.
/// </summary>
public sealed record StageOutcome
{
    /// <summary>
    /// Canonical name of the pipeline stage (e.g., "ASR", "Translation", "TTS").
    /// </summary>
    public required string StageName { get; init; }

    /// <summary>
    /// Execution status of this stage.
    /// </summary>
    public required StageStatus Status { get; init; }

    /// <summary>
    /// Timestamp when this stage started execution.
    /// </summary>
    public required DateTimeOffset StartTime { get; init; }

    /// <summary>
    /// Timestamp when this stage completed (successfully or otherwise).
    /// </summary>
    public required DateTimeOffset EndTime { get; init; }

    /// <summary>
    /// Relative paths to artifacts produced by this stage.
    /// Empty when the stage was skipped or failed before producing output.
    /// </summary>
    public required IReadOnlyList<string> ArtifactPaths { get; init; }

    /// <summary>
    /// Degradation descriptions recorded during stage execution.
    /// Null when no degradations occurred.
    /// </summary>
    public IReadOnlyList<string>? DegradationRecords { get; init; }

    /// <summary>
    /// Structured reason code for skip or failure
    /// (e.g., "BLOCKED_NON_COMMERCIAL", "PREREQUISITE_MISSING").
    /// Null when the stage succeeded.
    /// </summary>
    public string? ReasonCode { get; init; }
}
