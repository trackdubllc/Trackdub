using Trackdub.Application.Benchmarking;
using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class WorkingSetPeakMonitorFactoryTests
{
    [Fact]
    public async Task Default_factory_produces_a_working_peak_monitor()
    {
        var sampler = new SequenceSampler(100, 800, 1200);
        IWorkingSetPeakMonitorFactory factory = new WorkingSetPeakMonitorFactory();

        IWorkingSetPeakMonitor monitor = factory.Create(sampler, 100, TimeSpan.FromMilliseconds(5));

        await sampler.SecondSample.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1200, monitor.Stop());
        Assert.Null(monitor.UnavailableReason);
    }

    [Fact]
    public void Capture_delegates_monitor_lifetime_to_the_injected_factory()
    {
        var collector = new FixedCollector();
        var sampler = new FixedSampler(100);
        var factory = new RecordingFactory(peak: 500, reason: null);
        var capture = new StageResourceTelemetryCapture(
            collector,
            new ResourceTelemetryValidator(),
            new ResourceTelemetryBounds { MaxWorkingSetBytes = 400 },
            "measured",
            1,
            timing: null,
            workingSetSampler: sampler,
            workingSetSamplingInterval: TimeSpan.FromMilliseconds(7),
            monitorFactory: factory);

        capture.Report(new("asr", PipelineProgressEventKind.Started));
        capture.Report(new("asr", PipelineProgressEventKind.Completed));

        var created = Assert.Single(factory.Created);
        Assert.Same(sampler, created.Sampler);
        Assert.Equal(100, created.Initial);
        Assert.Equal(TimeSpan.FromMilliseconds(7), created.Interval);
        Assert.Equal(1, factory.Stub.StopCalls);
        BenchmarkStageResourceTelemetry sample = Assert.Single(capture.Snapshot());
        Assert.Equal(
            500d,
            Assert.Single(sample.Validation.Checks, check => check.Metric == "workingSetBytes").ObservedValue);
    }

    [Fact]
    public void Capture_propagates_monitor_unavailability_without_interrupting_the_stage()
    {
        var factory = new RecordingFactory(peak: null, reason: "probe failed");
        var capture = new StageResourceTelemetryCapture(
            new FixedCollector(),
            new ResourceTelemetryValidator(),
            new ResourceTelemetryBounds { MaxWorkingSetBytes = 400 },
            "measured",
            1,
            timing: null,
            workingSetSampler: new FixedSampler(100),
            workingSetSamplingInterval: null,
            monitorFactory: factory);

        capture.Report(new("asr", PipelineProgressEventKind.Started));
        capture.Report(new("asr", PipelineProgressEventKind.Completed));

        BenchmarkStageResourceTelemetry sample = Assert.Single(capture.Snapshot());
        Assert.Equal(BenchmarkEvidenceStatus.Completed, sample.ExecutionStatus);
        Assert.Equal(ResourceTelemetryStatus.Unavailable, sample.Validation.Status);
        Assert.Contains("probe failed", sample.Validation.Checks
            .Single(check => check.Metric == "workingSetBytes").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_never_contacts_the_factory_without_a_sampler()
    {
        var factory = new RecordingFactory(peak: 500, reason: null);
        var capture = new StageResourceTelemetryCapture(
            new FixedCollector(),
            new ResourceTelemetryValidator(),
            new(),
            "measured",
            1,
            timing: null,
            workingSetSampler: null,
            workingSetSamplingInterval: null,
            monitorFactory: factory);

        capture.Report(new("asr", PipelineProgressEventKind.Started));
        capture.Report(new("asr", PipelineProgressEventKind.Completed));

        Assert.Empty(factory.Created);
        Assert.Single(capture.Snapshot());
    }

    private sealed class RecordingFactory(long? peak, string? reason) : IWorkingSetPeakMonitorFactory
    {
        public List<(IWorkingSetSampler Sampler, long? Initial, TimeSpan? Interval)> Created { get; } = [];

        public StubMonitor Stub { get; } = new(peak, reason);

        public IWorkingSetPeakMonitor Create(IWorkingSetSampler sampler, long? initialValue, TimeSpan? interval)
        {
            Created.Add((sampler, initialValue, interval));
            return Stub;
        }
    }

    private sealed class StubMonitor(long? peak, string? reason) : IWorkingSetPeakMonitor
    {
        public int StopCalls { get; private set; }

        public string? UnavailableReason => reason;

        public long? Stop()
        {
            StopCalls++;
            return peak;
        }
    }

    private sealed class FixedSampler(long value) : IWorkingSetSampler
    {
        public long CaptureWorkingSetBytes() => value;
    }

    private sealed class SequenceSampler(params long[] values) : IWorkingSetSampler
    {
        private int index;
        public TaskCompletionSource SecondSample { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long CaptureWorkingSetBytes()
        {
            int current = Interlocked.Increment(ref index) - 1;
            if (current == 1) SecondSample.TrySetResult();
            return values[Math.Min(current, values.Length - 1)];
        }
    }

    private sealed class FixedCollector : IResourceTelemetryCollector
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
                WorkingSetBytes = 100,
                ManagedAllocatedBytes = sample * 10,
                AvailableVramMb = 500,
            };
        }
    }
}
