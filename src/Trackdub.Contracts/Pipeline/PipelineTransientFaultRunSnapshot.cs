using Trackdub.Domain.Pipeline;

namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Per-run, per-stage aggregation snapshot produced by
/// <see cref="Trackdub.Application.Transcripts.Pipeline.PipelineTransientFaultBus.SnapshotPerRun"/>.
/// Lives in Contracts alongside <see cref="TransientFaultSummary"/> because
/// the diagnostics-bundle transient section and the per-run reader share the
/// same shape; the upward dependency edge (Contracts -> Domain) is the only
/// edge needed. See <c>docs/internal/pipeline-readiness-spec.md</c> §9.1
/// (per-run aggregation recommendation) + §11.4 (test surface).
/// </summary>
public sealed record PipelineTransientFaultRunSnapshot(
    Guid ProjectId,
    IReadOnlyList<PipelineTransientFault> Faults,
    IReadOnlyList<string> StagesInArrivalOrder,
    IReadOnlyDictionary<string, IReadOnlyDictionary<TransientFailureKind, int>> CountsByStageAndKind)
{
    /// <summary>Total number of faults in the per-run filtered snapshot.</summary>
    public int TotalFaults => Faults.Count;
}
