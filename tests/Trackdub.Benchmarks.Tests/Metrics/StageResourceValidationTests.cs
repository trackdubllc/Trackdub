using Trackdub.Application.Benchmarking;
using Trackdub.Application.Dubbing;
using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class StageResourceValidationTests
{
    [Theory]
    [InlineData(PipelineProgressEventKind.Completed, BenchmarkEvidenceStatus.Completed)]
    [InlineData(PipelineProgressEventKind.Failed, BenchmarkEvidenceStatus.Failed)]
    [InlineData(PipelineProgressEventKind.Skipped, BenchmarkEvidenceStatus.Skipped)]
    public void All_stages_validate_terminal_samples(PipelineProgressEventKind terminal, BenchmarkEvidenceStatus expected)
    {
        var capture = Create();
        foreach (string stage in DubbingPipelineStages.ExtendedStageOrder)
        {
            capture.Report(new(stage, PipelineProgressEventKind.Started));
            capture.Report(new(stage, terminal, Message: "terminal reason"));
        }

        Assert.Equal(DubbingPipelineStages.ExtendedStageOrder.Count, capture.Snapshot().Count);
        Assert.All(capture.Snapshot(), sample =>
        {
            Assert.Equal(expected, sample.ExecutionStatus);
            Assert.Equal("terminal reason", sample.Reason);
            Assert.Equal(50d, Assert.Single(sample.Validation.Checks, check => check.Metric == "cpuPercent").ObservedValue);
            Assert.Equal(200d, sample.Validation.CpuTimeMilliseconds);
            Assert.Equal(100d, sample.Validation.ElapsedMilliseconds);
            Assert.Equal(4, sample.Validation.ProcessorCount);
        });
    }

    [Fact]
    public void Unstarted_skip_has_explicit_reason_without_measurement()
    {
        var capture = Create();
        capture.Report(new("asr", PipelineProgressEventKind.Skipped, Message: "model missing"));
        var sample = Assert.Single(capture.Snapshot());
        Assert.Equal(ResourceTelemetryStatus.Skipped, sample.Validation.Status);
        Assert.Equal("model missing", sample.Reason);
        Assert.All(sample.Validation.Checks, check => Assert.Null(check.ObservedValue));
    }

    [Fact]
    public void Missing_samples_degrade_without_losing_completed_execution()
    {
        var capture = Create(new MissingCollector());
        capture.Report(new("asr", PipelineProgressEventKind.Started));
        capture.Report(new("asr", PipelineProgressEventKind.Completed));
        var sample = Assert.Single(capture.Snapshot());
        Assert.Equal(BenchmarkEvidenceStatus.Completed, sample.ExecutionStatus);
        Assert.Equal(ResourceTelemetryStatus.Unavailable, sample.Validation.Status);
        Assert.All(sample.Validation.Checks, check => Assert.NotNull(check.Reason));
    }

    [Fact]
    public void Duplicate_terminal_does_not_reuse_previous_start()
    {
        var capture = Create();
        capture.Report(new("ASR", PipelineProgressEventKind.Started, StageKey: "asr-key"));
        capture.Report(new("asr", PipelineProgressEventKind.Completed, StageKey: "asr-key"));
        capture.Report(new("asr", PipelineProgressEventKind.Completed, StageKey: "asr-key"));
        Assert.Single(capture.Snapshot());
    }

    [Fact]
    public void Retry_keeps_each_attempt_and_original_failure()
    {
        var capture = Create();
        capture.Report(new("asr", PipelineProgressEventKind.Started));
        capture.Report(new("asr", PipelineProgressEventKind.Failed, Message: "first attempt failed"));
        capture.Report(new("asr", PipelineProgressEventKind.Started));
        capture.Report(new("asr", PipelineProgressEventKind.Completed));
        var samples = capture.Snapshot();
        Assert.Equal(2, samples.Count);
        Assert.Equal(1, samples[0].Attempt);
        Assert.Equal(2, samples[1].Attempt);
        Assert.Equal("first attempt failed", samples[0].Reason);
    }

    [Fact]
    public void Interrupted_stage_keeps_cpu_evidence_and_cancellation_reason()
    {
        var capture = Create();
        capture.Report(new("asr", PipelineProgressEventKind.Started));
        capture.CompletePending(BenchmarkEvidenceStatus.Canceled, "Canceled.");
        var sample = Assert.Single(capture.Snapshot());
        Assert.Equal(BenchmarkEvidenceStatus.Canceled, sample.ExecutionStatus);
        Assert.Equal("Canceled.", sample.Reason);
        Assert.Equal(200d, sample.Validation.CpuTimeMilliseconds);
    }

    [Fact]
    public void Exceeding_cpu_budget_records_explicit_failure()
    {
        var capture = Create(bounds: new() { MaxCpuPercent = 49 });
        capture.Report(new("asr", PipelineProgressEventKind.Started));
        capture.Report(new("asr", PipelineProgressEventKind.Completed));
        var sample = Assert.Single(capture.Snapshot());
        Assert.Equal(ResourceTelemetryStatus.Failed, sample.Validation.Status);
        var cpu = Assert.Single(sample.Validation.Checks, check => check.Metric == "cpuPercent");
        Assert.Equal(50d, cpu.ObservedValue);
        Assert.Equal(49d, cpu.Threshold);
        Assert.NotNull(cpu.Reason);
    }

    [Fact]
    public void Nested_events_do_not_close_the_enclosing_stage()
    {
        var capture = Create();
        capture.Report(new("translation", PipelineProgressEventKind.Started));
        capture.Report(new("translation", PipelineProgressEventKind.Started));
        capture.Report(new("translation", PipelineProgressEventKind.Completed));
        Assert.Empty(capture.Snapshot());
        capture.Report(new("translation", PipelineProgressEventKind.Failed, Message: "artifact write failed"));
        var sample = Assert.Single(capture.Snapshot());
        Assert.Equal(BenchmarkEvidenceStatus.Failed, sample.ExecutionStatus);
        Assert.Equal("artifact write failed", sample.Reason);
    }

    [Fact]
    public void Authoritative_outcome_reconciles_status_and_reason_without_losing_validation()
    {
        var capture = Create(bounds: new() { MaxCpuPercent = 49 });
        capture.Report(new("tts", PipelineProgressEventKind.Started));
        capture.Report(new("tts", PipelineProgressEventKind.Completed));
        capture.CompleteOutcomes([new()
        {
            StageName = "tts",
            Status = Trackdub.Contracts.Dubbing.StageStatus.PartiallySucceeded,
            ReasonCode = "PARTIAL_OUTPUT",
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow,
            ArtifactPaths = [],
        }]);
        var sample = Assert.Single(capture.Snapshot());
        Assert.Equal(BenchmarkEvidenceStatus.PartiallyCompleted, sample.ExecutionStatus);
        Assert.Equal("PARTIAL_OUTPUT", sample.Reason);
        Assert.Equal(ResourceTelemetryStatus.Failed, sample.Validation.Status);
    }

    [Fact]
    public void Reconciliation_keeps_the_specific_cause_and_the_queryable_code()
    {
        var capture = Create();
        // A stage the engine never started: the progress message names the blocking stage while
        // the outcome carries only the bare code. Neither fact may be lost.
        capture.Report(new("tts", PipelineProgressEventKind.Skipped,
            Message: "Skipped due to failed prerequisite: translation"));
        capture.CompleteOutcomes([new()
        {
            StageName = "tts",
            Status = Trackdub.Contracts.Dubbing.StageStatus.Skipped,
            ReasonCode = "PREREQUISITE_FAILED",
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow,
            ArtifactPaths = [],
        }]);

        var sample = Assert.Single(capture.Snapshot());
        Assert.Equal("PREREQUISITE_FAILED: Skipped due to failed prerequisite: translation", sample.Reason);
    }

    [Fact]
    public void Reconciliation_does_not_duplicate_a_code_already_named_by_the_message()
    {
        var capture = Create();
        const string reason = "Decoder refused the fixture.";
        capture.Report(new("audio-prep", PipelineProgressEventKind.Failed, Message: reason));
        capture.CompleteOutcomes([new()
        {
            StageName = "audio-prep",
            Status = Trackdub.Contracts.Dubbing.StageStatus.Failed,
            ReasonCode = reason,
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow,
            ArtifactPaths = [],
        }]);

        Assert.Equal(reason, Assert.Single(capture.Snapshot()).Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Sampler_failure_is_unavailable_and_never_interrupts_the_stage(int failAt)
    {
        var capture = Create(new ThrowingCollector(failAt));
        capture.Report(new("asr", PipelineProgressEventKind.Started));
        capture.Report(new("asr", PipelineProgressEventKind.Completed));
        var sample = Assert.Single(capture.Snapshot());
        Assert.Equal(BenchmarkEvidenceStatus.Completed, sample.ExecutionStatus);
        Assert.Equal(ResourceTelemetryStatus.Unavailable, sample.Validation.Status);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(sample), StringComparison.Ordinal);
    }

    private sealed class ThrowingCollector(int failAt) : IResourceTelemetryCollector
    {
        private int index;
        private readonly SequenceCollector collector = new();
        public ResourceUsageSnapshot Capture() => index++ == failAt
            ? throw new InvalidOperationException("Cannot read C:/private/metric") : collector.Capture();
    }

    private static StageResourceTelemetryCapture Create(IResourceTelemetryCollector? collector = null, ResourceTelemetryBounds? bounds = null) =>
        new(collector ?? new SequenceCollector(), new ResourceTelemetryValidator(), bounds ?? new(), "measured", 1);

    private sealed class MissingCollector : IResourceTelemetryCollector
    {
        public ResourceUsageSnapshot Capture() => new();
    }

    private sealed class SequenceCollector : IResourceTelemetryCollector
    {
        private int index;
        public ResourceUsageSnapshot Capture()
        {
            int sample = index++;
            return new()
            {
                CpuTimeMilliseconds = sample * 200d,
                MonotonicMilliseconds = sample * 100d,
                ProcessorCount = 4,
                WorkingSetBytes = 1000,
                ManagedAllocatedBytes = sample * 10,
                AvailableVramMb = 500,
            };
        }
    }
}
