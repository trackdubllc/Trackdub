using Trackdub.Application.Pipeline;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Tests.Pipeline;

public sealed class PipelineProgressEventContractTests
{
    [Fact]
    public void CompatibilityConstructor_LeavesOutputIdentityDefaults()
    {
        var progressEvent = new PipelineProgressEvent(
            StageName: "asr",
            EventKind: PipelineProgressEventKind.Started,
            Percentage: 0d,
            Message: null,
            ElapsedDuration: TimeSpan.Zero);

        Assert.Null(progressEvent.RunId);
        Assert.Equal(0, progressEvent.SequenceNumber);
        Assert.Null(progressEvent.OutputKind);
        Assert.Null(progressEvent.ItemIndex);
        Assert.Null(progressEvent.RevisionId);
        Assert.Null(progressEvent.SegmentId);
        Assert.Null(progressEvent.ArtifactId);
    }

    [Fact]
    public void WideConstructor_PreservesOutputIdentityValues()
    {
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var progressEvent = new PipelineProgressEvent(
            StageName: "tts",
            EventKind: PipelineProgressEventKind.Progress,
            Phase: "Output available",
            OutputKind: PipelineOutputKind.PlayableAudioPersisted,
            ItemIndex: 3,
            RunId: runId,
            SequenceNumber: 42,
            RevisionId: revisionId,
            SegmentId: segmentId,
            ArtifactId: artifactId);

        Assert.Equal(runId, progressEvent.RunId);
        Assert.Equal(42, progressEvent.SequenceNumber);
        Assert.Equal(PipelineOutputKind.PlayableAudioPersisted, progressEvent.OutputKind);
        Assert.Equal(3, progressEvent.ItemIndex);
        Assert.Equal(revisionId, progressEvent.RevisionId);
        Assert.Equal(segmentId, progressEvent.SegmentId);
        Assert.Equal(artifactId, progressEvent.ArtifactId);
    }

    [Fact]
    public void RunScopedProgressReporter_StampsRunIdAndIncreasingSequence()
    {
        var runId = Guid.NewGuid();
        var inner = new CollectingProgress();
        var reporter = new RunScopedProgressReporter(runId, inner);

        reporter.Report(new PipelineProgressEvent("asr", PipelineProgressEventKind.Started));
        reporter.Report(new PipelineProgressEvent("asr", PipelineProgressEventKind.Progress));
        reporter.Report(new PipelineProgressEvent("asr", PipelineProgressEventKind.Completed));

        Assert.Equal(3, inner.Events.Count);
        Assert.All(inner.Events, e => Assert.Equal(runId, e.RunId));
        Assert.Equal([1L, 2L, 3L], inner.Events.Select(e => e.SequenceNumber).ToArray());
    }

    [Fact]
    public void RunScopedProgressReporter_OverwritesPreStampedIdentity()
    {
        var runId = Guid.NewGuid();
        var foreignRunId = Guid.NewGuid();
        var inner = new CollectingProgress();
        var reporter = new RunScopedProgressReporter(runId, inner);

        reporter.Report(new PipelineProgressEvent(
            "asr", PipelineProgressEventKind.Progress, RunId: foreignRunId, SequenceNumber: 99));
        reporter.Report(new PipelineProgressEvent("asr", PipelineProgressEventKind.Progress));

        Assert.Equal(runId, inner.Events[0].RunId);
        Assert.Equal(1, inner.Events[0].SequenceNumber);
        Assert.Equal(runId, inner.Events[1].RunId);
        Assert.Equal(2, inner.Events[1].SequenceNumber);
    }

    [Fact]
    public void RunScopedProgressReporter_ConcurrentReports_DeliverSequenceOneToN()
    {
        var runId = Guid.NewGuid();
        var inner = new CollectingProgress();
        var reporter = new RunScopedProgressReporter(runId, inner);

        Parallel.For(0, 100, _ =>
            reporter.Report(new PipelineProgressEvent("asr", PipelineProgressEventKind.Progress)));

        Assert.Equal(100, inner.Events.Count);
        Assert.All(inner.Events, e => Assert.Equal(runId, e.RunId));
        Assert.Equal(
            Enumerable.Range(1, 100).Select(i => (long)i).OrderBy(i => i).ToArray(),
            inner.Events.Select(e => e.SequenceNumber).OrderBy(i => i).ToArray());
    }

    [Fact]
    public void RunScopedProgressReporter_NullInner_IsNoOp()
    {
        var reporter = new RunScopedProgressReporter(Guid.NewGuid(), inner: null);

        reporter.Report(new PipelineProgressEvent("asr", PipelineProgressEventKind.Started));
        Assert.Throws<ArgumentNullException>(() => reporter.Report(null!));
    }

    private sealed class CollectingProgress : IProgress<PipelineProgressEvent>
    {
        public List<PipelineProgressEvent> Events { get; } = [];

        public void Report(PipelineProgressEvent value)
        {
            lock (Events)
            {
                Events.Add(value);
            }
        }
    }
}
