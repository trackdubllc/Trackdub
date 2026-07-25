namespace Trackdub.Sdk;

/// <summary>
/// Structured summary emitted after batch processing completes,
/// listing per-file outcomes and aggregate counts.
/// </summary>
public sealed record BatchReport
{
    /// <summary>Per-file outcomes in processing order.</summary>
    public required IReadOnlyList<BatchFileOutcome> Files { get; init; }

    /// <summary>Number of files that completed successfully.</summary>
    public required int SucceededCount { get; init; }

    /// <summary>Number of files that failed during pipeline execution.</summary>
    public required int FailedCount { get; init; }

    /// <summary>Number of files skipped due to fail-fast halt.</summary>
    public required int SkippedCount { get; init; }
}
