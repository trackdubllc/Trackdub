using Trackdub.Domain.Pipeline;

namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Run correlation triple (id + start/end UTC) carried on a
/// <see cref="TransientFaultSummary"/> so the diagnostics bundle can scope
/// the snapshot to a single dubbing run. Lives alongside the summary so the
/// JSON shape stays self-describing. See
/// <c>docs/internal/pipeline-readiness-spec.md</c> §9.1 ADR-CAND
/// <c>pipeline-transient-aggregation</c> + §11.4 validation ladder.
/// </summary>
public sealed record TransientRunInfo
{
    public Guid RunId { get; init; }
    public DateTimeOffset RunStartUtc { get; init; }
    public DateTimeOffset? RunEndUtc { get; init; }

    public TransientRunInfo(Guid runId, DateTimeOffset runStartUtc, DateTimeOffset? runEndUtc)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("RunId must be a non-empty Guid.", nameof(runId));
        }

        if (runEndUtc.HasValue && runEndUtc.Value < runStartUtc)
        {
            throw new ArgumentException("RunEndUtc must be greater than or equal to RunStartUtc.", nameof(runEndUtc));
        }

        RunId = runId;
        RunStartUtc = runStartUtc;
        RunEndUtc = runEndUtc;
    }
}

/// <summary>
/// Aggregated snapshot of transient faults shipped during a single dubbing
/// pipeline run. Persisted into the diagnostics bundle as the top-level
/// <c>transient-fault-summary.json</c> entry. See
/// <c>docs/internal/pipeline-readiness-spec.md</c> section 4.5 + 11.3.
/// Lives in Contracts (alongside <see cref="PipelineTransientFault"/>) so
/// the diagnostics request DTO and the snapshot shape travel together; the
/// dependency direction Contracts → Domain is the only upward edge needed.
/// Optional <see cref="RunInfo"/> correlates the snapshot to a single run
/// per §9.1 (callers without run context leave it null, e.g. crash handlers).
/// </summary>
public sealed record TransientFaultSummary(
    int Total,
    IReadOnlyDictionary<TransientFailureKind, int> CountsByKind,
    IReadOnlyList<PipelineTransientFault> MostRecent,
    TransientRunInfo? RunInfo = null)
{
    /// <summary>Default cap for <see cref="MostRecent"/> to keep the JSON compact.</summary>
    public const int DefaultMostRecentCap = 20;

    /// <summary>Run identifier when <see cref="RunInfo"/> is present; otherwise <see cref="Guid.Empty"/>.</summary>
    public Guid RunId => RunInfo?.RunId ?? Guid.Empty;

    /// <summary>Run start time when <see cref="RunInfo"/> is present; otherwise <c>null</c>.</summary>
    public DateTimeOffset? StartedAt => RunInfo?.RunStartUtc;

    /// <summary>Run end time when <see cref="RunInfo"/> is present and known; otherwise <c>null</c>.</summary>
    public DateTimeOffset? EndedAt => RunInfo?.RunEndUtc;

    /// <summary>
    /// Builds a summary from <paramref name="snapshot"/>, taking the last
    /// <paramref name="mostRecentCap"/> items in arrival order and aggregating
    /// counts via <see cref="PipelineTransientFault.Kind"/>. Optional
    /// <paramref name="runInfo"/> correlates the snapshot to a single run.
    /// </summary>
    public static TransientFaultSummary From(
        IReadOnlyList<PipelineTransientFault> snapshot,
        TransientRunInfo? runInfo = null,
        int mostRecentCap = DefaultMostRecentCap)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mostRecentCap);

        if (runInfo is not null)
        {
            if (runInfo.RunId == Guid.Empty)
            {
                throw new ArgumentException("RunInfo.RunId must be a non-empty Guid.", nameof(runInfo));
            }

            if (runInfo.RunEndUtc is DateTimeOffset end && end < runInfo.RunStartUtc)
            {
                throw new ArgumentException("RunInfo.RunEndUtc must be greater than or equal to RunStartUtc.", nameof(runInfo));
            }
        }

        var counts = new Dictionary<TransientFailureKind, int>();
        foreach (PipelineTransientFault fault in snapshot)
        {
            counts.TryGetValue(fault.Kind, out int existing);
            counts[fault.Kind] = existing + 1;
        }

        PipelineTransientFault[] mostRecent;
        if (snapshot.Count <= mostRecentCap)
        {
            mostRecent = snapshot.ToArray();
        }
        else
        {
            mostRecent = new PipelineTransientFault[mostRecentCap];
            int startIndex = snapshot.Count - mostRecentCap;
            for (int i = 0; i < mostRecentCap; i++)
            {
                mostRecent[i] = snapshot[startIndex + i];
            }
        }

        return new TransientFaultSummary(
            Total: snapshot.Count,
            CountsByKind: counts,
            MostRecent: mostRecent,
            RunInfo: runInfo);
    }

    /// <summary>
    /// Builds a run-tied summary from <paramref name="snapshot"/>, taking the
    /// last <paramref name="mostRecentCap"/> items in arrival order and
    /// aggregating counts via <see cref="PipelineTransientFault.Kind"/>.
    /// <paramref name="runId"/> identifies the run this summary belongs to
    /// (cross-run disambiguation key). <paramref name="startedAt"/> anchors
    /// the run start (<c>UTC</c>). <paramref name="endedAt"/> may be
    /// <c>null</c> when the run crashed before reaching a clean termination.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="runId"/> is <see cref="Guid.Empty"/>, or
    /// <paramref name="endedAt"/> is earlier than <paramref name="startedAt"/>.</exception>
    public static TransientFaultSummary From(
        IReadOnlyList<PipelineTransientFault> snapshot,
        Guid runId,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt = null,
        int mostRecentCap = DefaultMostRecentCap)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("RunId must be a non-empty Guid for run-tied summary aggregation.", nameof(runId));
        }

        if (endedAt.HasValue && endedAt.Value < startedAt)
        {
            throw new ArgumentException("EndedAt must be greater than or equal to StartedAt.", nameof(endedAt));
        }

        return From(snapshot, new TransientRunInfo(runId, startedAt, endedAt), mostRecentCap);
    }
}
