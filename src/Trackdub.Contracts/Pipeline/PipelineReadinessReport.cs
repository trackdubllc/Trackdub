namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Full readiness report for a pipeline run, covering all enabled stages.
/// Produced by <see cref="IPipelineReadinessService"/> from a set of draft or
/// frozen runtime model selections.
/// </summary>
public sealed record PipelineReadinessReport(
    IReadOnlyList<StageReadiness> Stages)
{
    /// <summary>
    /// True when all stages are in a non-blocking state (Ready, Satisfied, or SkippableOptional).
    /// A run may proceed only when this is true.
    /// </summary>
    public bool IsRunReady => Stages.All(s => !s.Status.IsBlocking());

    /// <summary>Returns all stages in a blocking state.</summary>
    public IEnumerable<StageReadiness> BlockingStages =>
        Stages.Where(s => s.Status.IsBlocking());

    /// <summary>Empty report — used as initial / loading placeholder.</summary>
    public static PipelineReadinessReport Empty { get; } = new([]);
}
