using Trackdub.Domain.Pipeline;

namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// A typed transient-fault record emitted by the dubbing pipeline and consumed
/// by the <c>PipelineTransientFaultBus</c>. See
/// <c>docs/internal/pipeline-readiness-spec.md</c> §4.2 + §4.3 for the
/// consumer contract and §11.3 for the test surface.
/// </summary>
public sealed record PipelineTransientFault
{
    /// <summary>
    /// Creates a transient-fault record. Subject to validation; see argument-level null checks.
    /// </summary>
    public PipelineTransientFault(
        Guid projectId,
        string stageName,
        TransientFailureKind kind,
        string detail,
        DateTimeOffset happenedAt,
        int attemptNumber,
        IReadOnlyDictionary<string, string>? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        ArgumentOutOfRangeException.ThrowIfNegative(attemptNumber);

        ProjectId = projectId;
        StageName = stageName;
        Kind = kind;
        Detail = detail ?? string.Empty;
        HappenedAt = happenedAt;
        AttemptNumber = attemptNumber;
        Context = context;
    }

    /// <summary>The project this fault belongs to (filter key for the bus snapshot).</summary>
    public Guid ProjectId { get; init; }

    /// <summary>The pipeline stage that produced the fault (e.g. <c>Vad</c>, <c>Asr</c>, <c>Export</c>).</summary>
    public string StageName { get; init; }

    /// <summary>Classified transient kind; see <see cref="Trackdub.Domain.Pipeline.TransientFailureKind"/>.</summary>
    public TransientFailureKind Kind { get; init; }

    /// <summary>Free-form human-readable detail (exception message, exit code, etc.).</summary>
    public string Detail { get; init; }

    /// <summary>Moment the fault was raised (UTC).</summary>
    public DateTimeOffset HappenedAt { get; init; }

    /// <summary>One-based attempt counter within the same stage invocation.</summary>
    public int AttemptNumber { get; init; }

    /// <summary>Optional free-form context dictionary (paths, exception types, exit codes).</summary>
    public IReadOnlyDictionary<string, string>? Context { get; init; }
}
