using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Pipeline;
using Xunit;

namespace Trackdub.Infrastructure.Tests.Diagnostics;

/// <summary>
/// Aggregated snapshot coverage for <see cref="TransientFaultSummary"/>. Mirrors
/// spec §11.3 + §4.5 — MostRecent cap of 20, total equals sum of counts,
/// arrival order preserved through JSON round-trip, run correlation triple
/// (RunId/StartedAt/EndedAt) carried on the summary for §9.x per-run diagnostics.
/// </summary>
public sealed class TransientFaultSummaryTests
{
    [Fact]
    public void Empty_snapshot_yields_zero_total_and_empty_collections()
    {
        TransientFaultSummary summary = TransientFaultSummary.From(Array.Empty<PipelineTransientFault>());
        Assert.Equal(0, summary.Total);
        Assert.Empty(summary.CountsByKind);
        Assert.Empty(summary.MostRecent);
    }

    [Fact]
    public void Total_equals_sum_of_counts_by_kind()
    {
        var snapshot = new List<PipelineTransientFault>
        {
            Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, 1),
            Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, 2),
            Make(Guid.NewGuid(), "Export", TransientFailureKind.DirectoryLock, 3),
            Make(Guid.NewGuid(), "Export", TransientFailureKind.UserCancellation, 4),
            Make(Guid.NewGuid(), "Tts", TransientFailureKind.MemoryExhausted, 5),
        };

        TransientFaultSummary summary = TransientFaultSummary.From(snapshot);
        Assert.Equal(5, summary.Total);
        int sum = summary.CountsByKind.Values.Sum();
        Assert.Equal(summary.Total, sum);
    }

    [Fact]
    public void Snapshot_smaller_than_cap_returns_full_snapshot_in_arrival_order()
    {
        var snapshot = new List<PipelineTransientFault>
        {
            Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, 1),
            Make(Guid.NewGuid(), "Asr", TransientFailureKind.SqliteBusy, 2),
            Make(Guid.NewGuid(), "Export", TransientFailureKind.DirectoryLock, 3),
        };

        TransientFaultSummary summary = TransientFaultSummary.From(snapshot, mostRecentCap: 20);
        Assert.Equal(3, summary.MostRecent.Count);
        Assert.Same(snapshot[0], summary.MostRecent[0]);
        Assert.Same(snapshot[2], summary.MostRecent[2]);
    }

    [Fact]
    public void Snapshot_larger_than_cap_returns_last_n_in_arrival_order()
    {
        int total = 25;
        var snapshot = new List<PipelineTransientFault>(capacity: total);
        for (int i = 0; i < total; i++)
        {
            snapshot.Add(Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, i));
        }

        TransientFaultSummary summary = TransientFaultSummary.From(snapshot, mostRecentCap: 20);
        Assert.Equal(25, summary.Total);
        Assert.Equal(20, summary.MostRecent.Count);
        Assert.Equal(5, summary.MostRecent[0].AttemptNumber);
        Assert.Equal(24, summary.MostRecent[19].AttemptNumber);
    }

    [Fact]
    public void MostRecentCap_validation_rejects_zero_and_negative()
    {
        var snapshot = new List<PipelineTransientFault> { Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, 1) };
        Assert.Throws<ArgumentOutOfRangeException>(() => TransientFaultSummary.From(snapshot, mostRecentCap: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransientFaultSummary.From(snapshot, mostRecentCap: -1));
    }

    [Fact]
    public void From_rejects_null_snapshot()
    {
        Assert.Throws<ArgumentNullException>(() => TransientFaultSummary.From(null!));
    }

    [Fact]
    public void Default_most_recent_cap_is_20()
    {
        int total = TransientFaultSummary.DefaultMostRecentCap + 5;
        var snapshot = new List<PipelineTransientFault>(capacity: total);
        for (int i = 0; i < total; i++)
        {
            snapshot.Add(Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, i));
        }

        TransientFaultSummary summary = TransientFaultSummary.From(snapshot);
        Assert.Equal(TransientFaultSummary.DefaultMostRecentCap, summary.MostRecent.Count);
    }

    [Fact]
    public void From_without_run_info_leaves_RunInfo_null()
    {
        var snapshot = new List<PipelineTransientFault>
        {
            Make(Guid.NewGuid(), "Asr", TransientFailureKind.SqliteBusy, 1),
        };

        TransientFaultSummary summary = TransientFaultSummary.From(snapshot);

        // Default source path: callers without run context (e.g. global crash
        // handler outside an active dubbing run) do not have a run correlation
        // and leave the optional field null. JSON serialization will emit
        // "RunInfo": null in this case.
        Assert.Null(summary.RunInfo);
    }

    [Fact]
    public void From_with_run_info_propagates_RunIdStartEnd_into_summary()
    {
        Guid runId = Guid.NewGuid();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddSeconds(-30);
        DateTimeOffset end = DateTimeOffset.UtcNow;
        var runInfo = new TransientRunInfo(runId, start, end);

        var snapshot = new List<PipelineTransientFault>
        {
            Make(Guid.NewGuid(), "Asr", TransientFailureKind.SqliteBusy, 1),
            Make(Guid.NewGuid(), "Asr", TransientFailureKind.DirectoryLock, 2),
        };

        TransientFaultSummary summary = TransientFaultSummary.From(snapshot, runInfo);

        Assert.NotNull(summary.RunInfo);
        Assert.Equal(runId, summary.RunInfo!.RunId);
        Assert.Equal(start, summary.RunInfo.RunStartUtc);
        Assert.Equal(end, summary.RunInfo.RunEndUtc);
        // Counts and MostRecent remain bound to the snapshot, not the runInfo.
        Assert.Equal(2, summary.Total);
    }

    [Fact]
    public void From_rejects_empty_run_id()
    {
        var snapshot = new List<PipelineTransientFault> { Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, 1) };

        Assert.Throws<ArgumentException>(() => TransientFaultSummary.From(
            snapshot,
            runId: Guid.Empty,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            endedAt: DateTimeOffset.UtcNow));
    }

    [Fact]
    public void From_rejects_ended_at_before_started_at()
    {
        var snapshot = Array.Empty<PipelineTransientFault>();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset endedAt = startedAt.AddMinutes(-1);

        Assert.Throws<ArgumentException>(() => TransientFaultSummary.From(
            snapshot,
            runId: Guid.NewGuid(),
            startedAt: startedAt,
            endedAt: endedAt));
    }

    [Fact]
    public void From_accepts_null_ended_at_for_crash_mid_run()
    {
        var snapshot = Array.Empty<PipelineTransientFault>();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        TransientFaultSummary summary = TransientFaultSummary.From(
            snapshot,
            runId: Guid.NewGuid(),
            startedAt: startedAt,
            endedAt: null);

        Assert.Equal(startedAt, summary.StartedAt);
        Assert.Null(summary.EndedAt);
        Assert.False(summary.EndedAt.HasValue);
    }

    [Fact]
    public void From_carries_run_correlation_triple_through_to_summary()
    {
        var snapshot = new List<PipelineTransientFault> { Make(Guid.NewGuid(), "Asr", TransientFailureKind.SqliteBusy, 1) };
        Guid runId = Guid.NewGuid();
        DateTimeOffset startedAt = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset endedAt = new DateTimeOffset(2026, 7, 23, 10, 5, 0, TimeSpan.Zero);

        TransientFaultSummary summary = TransientFaultSummary.From(
            snapshot,
            runId: runId,
            startedAt: startedAt,
            endedAt: endedAt);

        Assert.Equal(runId, summary.RunId);
        Assert.Equal(startedAt, summary.StartedAt);
        Assert.Equal(endedAt, summary.EndedAt);
    }

    [Fact]
    public void TransientRunInfo_rejects_empty_run_id()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => new TransientRunInfo(Guid.Empty, start, null));
    }

    [Fact]
    public void TransientRunInfo_rejects_ended_at_before_started_at()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        DateTimeOffset end = start.AddMinutes(-1);

        Assert.Throws<ArgumentException>(() => new TransientRunInfo(Guid.NewGuid(), start, end));
    }

    [Fact]
    public void From_with_run_info_rejects_invalid_run_info()
    {
        var snapshot = Array.Empty<PipelineTransientFault>();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var valid = new TransientRunInfo(Guid.NewGuid(), start, start.AddMinutes(1));
        var invalid = valid with { RunEndUtc = start.AddMinutes(-1) };

        Assert.Throws<ArgumentException>(() => TransientFaultSummary.From(snapshot, invalid));
    }

    [Fact]
    public void Correlation_properties_default_to_empty_and_null_without_run_info()
    {
        var snapshot = new List<PipelineTransientFault>
        {
            Make(Guid.NewGuid(), "Asr", TransientFailureKind.SqliteBusy, 1),
        };

        // RunId/StartedAt/EndedAt are computed from RunInfo; callers without run
        // context (e.g. a global crash handler outside an active dubbing run,
        // see From_without_run_info_leaves_RunInfo_null) must still observe a
        // well-defined fallback rather than a null-reference on RunInfo.
        TransientFaultSummary summary = TransientFaultSummary.From(snapshot);

        Assert.Equal(Guid.Empty, summary.RunId);
        Assert.Null(summary.StartedAt);
        Assert.Null(summary.EndedAt);
    }

    [Fact]
    public void From_accepts_ended_at_equal_to_started_at()
    {
        // Boundary of the "EndedAt must be greater than or equal to StartedAt"
        // contract: a run that starts and ends within the same instant (e.g. an
        // immediate no-op run) must not be rejected by the strict "<" check.
        var snapshot = Array.Empty<PipelineTransientFault>();
        DateTimeOffset instant = DateTimeOffset.UtcNow;

        TransientFaultSummary summary = TransientFaultSummary.From(
            snapshot,
            runId: Guid.NewGuid(),
            startedAt: instant,
            endedAt: instant);

        Assert.Equal(instant, summary.StartedAt);
        Assert.Equal(instant, summary.EndedAt);
    }

    private static PipelineTransientFault Make(
        Guid projectId,
        string stageName,
        TransientFailureKind kind,
        int attempt) =>
        new(projectId, stageName, kind, $"d {attempt}", DateTimeOffset.UtcNow, attempt);
}
