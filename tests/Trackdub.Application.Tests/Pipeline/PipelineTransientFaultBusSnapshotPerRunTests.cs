using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Pipeline;

namespace Trackdub.Application.Tests.Pipeline;

/// <summary>
/// Per-run snapshot aggregation tests for <see cref="PipelineTransientFaultBus"/>.
/// Validates the <see cref="PipelineTransientFaultBus.SnapshotPerRun"/> reader
/// surfaced by <c>docs/internal/pipeline-readiness-spec.md</c> §9.1 (per-run
/// aggregation recommendation, option (b)) and §11.4 (test surface).
/// </summary>
public sealed class PipelineTransientFaultBusSnapshotPerRunTests
{
    [Fact]
    public void SnapshotPerRun_returns_only_faults_with_matching_projectId()
    {
        var bus = new PipelineTransientFaultBus();
        Guid projectA = Guid.NewGuid();
        Guid projectB = Guid.NewGuid();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        bus.Publish(new PipelineTransientFault(projectA, "Vad", TransientFailureKind.UserCancellation, "a1", t0, 1));
        bus.Publish(new PipelineTransientFault(projectB, "Asr", TransientFailureKind.DirectoryLock, "b1", t0.AddSeconds(1), 1));
        bus.Publish(new PipelineTransientFault(projectA, "Asr", TransientFailureKind.DirectoryLock, "a2", t0.AddSeconds(2), 1));
        bus.Publish(new PipelineTransientFault(projectA, "Asr", TransientFailureKind.DirectoryLock, "a3", t0.AddSeconds(3), 2));
        bus.Publish(new PipelineTransientFault(projectA, "Export", TransientFailureKind.UserCancellation, "a4", t0.AddSeconds(4), 1));

        PipelineTransientFaultRunSnapshot snapshot = bus.SnapshotPerRun(projectA);

        Assert.Equal(projectA, snapshot.ProjectId);
        Assert.Equal(4, snapshot.TotalFaults);
        Assert.Equal(4, snapshot.Faults.Count);
        Assert.All(snapshot.Faults, fault => Assert.Equal(projectA, fault.ProjectId));
        Assert.DoesNotContain(snapshot.Faults, fault => fault.ProjectId == projectB);

        // Idempotency: a second call returns the same shape (ring buffer is the source of truth).
        PipelineTransientFaultRunSnapshot second = bus.SnapshotPerRun(projectA);

        // Totals and high-level shape
        Assert.Equal(snapshot.TotalFaults, second.TotalFaults);
        Assert.Equal(snapshot.StagesInArrivalOrder, second.StagesInArrivalOrder);

        // Full snapshot equality: verify no extra state leaks between runs
        Assert.Equal(snapshot.Faults, second.Faults); // order and contents
        Assert.Equal(snapshot.CountsByStageAndKind, second.CountsByStageAndKind); // exact grouping equality
    }

    [Fact]
    public void SnapshotPerRun_groups_by_stage_then_kind_in_arrival_order()
    {
        var bus = new PipelineTransientFaultBus();
        Guid projectA = Guid.NewGuid();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        bus.Publish(new PipelineTransientFault(projectA, "Vad", TransientFailureKind.UserCancellation, "vad-1", t0, 1));
        bus.Publish(new PipelineTransientFault(projectA, "Asr", TransientFailureKind.DirectoryLock, "asr-1", t0.AddSeconds(1), 1));
        bus.Publish(new PipelineTransientFault(projectA, "Asr", TransientFailureKind.DirectoryLock, "asr-2", t0.AddSeconds(2), 2));
        bus.Publish(new PipelineTransientFault(projectA, "Export", TransientFailureKind.UserCancellation, "exp-1", t0.AddSeconds(3), 1));

        PipelineTransientFaultRunSnapshot snapshot = bus.SnapshotPerRun(projectA);

        // Stages preserve first-seen arrival order rather than alphabetical.
        Assert.Equal(new[] { "Vad", "Asr", "Export" }, snapshot.StagesInArrivalOrder);

        // Per-stage counts group by TransientFailureKind within the stage.
        Assert.Single(snapshot.CountsByStageAndKind["Vad"]);
        Assert.Equal(1, snapshot.CountsByStageAndKind["Vad"][TransientFailureKind.UserCancellation]);
        Assert.Single(snapshot.CountsByStageAndKind["Asr"]);
        Assert.Equal(2, snapshot.CountsByStageAndKind["Asr"][TransientFailureKind.DirectoryLock]);
        Assert.Single(snapshot.CountsByStageAndKind["Export"]);
        Assert.Equal(1, snapshot.CountsByStageAndKind["Export"][TransientFailureKind.UserCancellation]);

        // Fault list preserves arrival order across all stages.
        Assert.Collection(
            snapshot.Faults,
            f => Assert.Equal("Vad", f.StageName),
            f => Assert.Equal("Asr", f.StageName),
            f => Assert.Equal("Asr", f.StageName),
            f => Assert.Equal("Export", f.StageName));
    }

    [Fact]
    public void SnapshotPerRun_returns_empty_snapshot_when_no_faults_match_project()
    {
        var bus = new PipelineTransientFaultBus();
        Guid projectA = Guid.NewGuid();
        Guid projectB = Guid.NewGuid();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        bus.Publish(new PipelineTransientFault(projectA, "Vad", TransientFailureKind.UserCancellation, "a1", t0, 1));

        PipelineTransientFaultRunSnapshot snapshot = bus.SnapshotPerRun(projectB);

        Assert.Equal(projectB, snapshot.ProjectId);
        Assert.Equal(0, snapshot.TotalFaults);
        Assert.Empty(snapshot.Faults);
        Assert.Empty(snapshot.StagesInArrivalOrder);
        Assert.Empty(snapshot.CountsByStageAndKind);
    }

    [Fact]
    public void SnapshotPerRun_throws_for_empty_project_id()
    {
        var bus = new PipelineTransientFaultBus();
        Assert.Throws<ArgumentOutOfRangeException>(() => bus.SnapshotPerRun(Guid.Empty));
    }

    [Fact]
    public void SnapshotPerRun_CountsByStageAndKind_outer_and_inner_maps_are_immutable()
    {
        var bus = new PipelineTransientFaultBus();
        Guid projectId = Guid.NewGuid();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        bus.Publish(new PipelineTransientFault(projectId, "Vad", TransientFailureKind.UserCancellation, "vad-1", t0, 1));
        bus.Publish(new PipelineTransientFault(projectId, "Asr", TransientFailureKind.DirectoryLock, "asr-1", t0.AddSeconds(1), 1));

        PipelineTransientFaultRunSnapshot snapshot = bus.SnapshotPerRun(projectId);

        // Outer dictionary should be immutable.
        var outerDict = (IDictionary<string, IReadOnlyDictionary<TransientFailureKind, int>>)snapshot.CountsByStageAndKind;
        Assert.Throws<NotSupportedException>(() => outerDict.Add("NewStage", new Dictionary<TransientFailureKind, int>()));
        Assert.Throws<NotSupportedException>(() => outerDict["Vad"] = new Dictionary<TransientFailureKind, int>());
        Assert.Throws<NotSupportedException>(() => outerDict.Clear());

        // Inner dictionary should be immutable.
        var innerDict = (IDictionary<TransientFailureKind, int>)snapshot.CountsByStageAndKind["Vad"];
        Assert.Throws<NotSupportedException>(() => innerDict.Add(TransientFailureKind.DirectoryLock, 1));
        Assert.Throws<NotSupportedException>(() => innerDict[TransientFailureKind.UserCancellation] = 999);
        Assert.Throws<NotSupportedException>(() => innerDict.Clear());

        // Faults list should be immutable.
        var faults = (IList<PipelineTransientFault>)snapshot.Faults;
        Assert.Throws<NotSupportedException>(() => faults.Add(new PipelineTransientFault(projectId, "Vad", TransientFailureKind.UserCancellation, "vad-2", t0, 2)));
        Assert.Throws<NotSupportedException>(() => faults[0] = new PipelineTransientFault(projectId, "Vad", TransientFailureKind.UserCancellation, "vad-2", t0, 2));
        Assert.Throws<NotSupportedException>(() => faults.Clear());

        // Stage arrival list should be immutable.
        var stages = (IList<string>)snapshot.StagesInArrivalOrder;
        Assert.Throws<NotSupportedException>(() => stages.Add("NewStage"));
        Assert.Throws<NotSupportedException>(() => stages[0] = "MutatedStage");
        Assert.Throws<NotSupportedException>(() => stages.Clear());
    }
}
