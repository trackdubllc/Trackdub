using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class ResourceTelemetryAggregatorTests
{
    [Fact]
    public void Aggregate_summarizes_each_metric_across_iterations()
    {
        ResourceTelemetryDistribution distribution = Assert.Single(Aggregate(
        [
            Sample(1, 10d, 100d, 1d),
            Sample(2, 20d, 200d, 2d),
            Sample(3, 30d, 300d, 3d),
        ]));

        Assert.Equal("audio-prep", distribution.Stage);
        Assert.Equal("measured", distribution.Phase);
        Assert.Equal(3, distribution.IterationCount);
        ResourceMetricStatistics cpu = Metric(distribution, "cpuPercent");
        Assert.Equal(3, cpu.SampleCount);
        Assert.Equal(0, cpu.UnavailableSampleCount);
        Assert.Equal(0, cpu.FailingSampleCount);
        Assert.Equal(10d, cpu.Minimum);
        Assert.Equal(30d, cpu.Maximum);
        Assert.Equal(20d, cpu.Mean);
        Assert.Equal(20d, cpu.P50);
        Assert.Equal(ResourceTelemetryStatus.Passed, cpu.Status);
    }

    [Fact]
    public void Aggregate_keeps_an_outlier_visible_without_masquerading_as_typical()
    {
        // Nine healthy iterations and one spike: the distribution must show both, and the failing
        // count must say 1 of 10 rather than letting the spike represent the run.
        BenchmarkStageResourceTelemetry[] samples =
        [
            .. Enumerable.Range(1, 9).Select(i => Sample(i, 10d, 100d, 1d)),
            Sample(10, 95d, 950d, 9d),
        ];

        ResourceMetricStatistics cpu = Metric(Assert.Single(Aggregate(samples)), "cpuPercent");

        Assert.Equal(10, cpu.SampleCount);
        Assert.Equal(1, cpu.FailingSampleCount);
        Assert.Equal(95d, cpu.Maximum);
        Assert.Equal(10d, cpu.P50);
        // Type 7 interpolates at position 0.95 * 9 = 8.55, between the 9th (10) and 10th (95)
        // samples: 10 + (95 - 10) * 0.55 = 56.75. The spike is visible in the tail without
        // dragging the median.
        Assert.Equal(56.75d, cpu.P95, 10);
        Assert.Equal(ResourceTelemetryStatus.Failed, cpu.Status);
    }

    [Fact]
    public void Aggregate_separates_phases_so_warmup_does_not_pollute_the_measured_distribution()
    {
        ResourceTelemetryDistribution measured = Assert.Single(
            Aggregate(
            [
                Sample(1, 40d, 400d, 4d, phase: "warmup"),
                Sample(1, 10d, 100d, 1d, phase: "measured"),
                Sample(2, 12d, 120d, 1d, phase: "measured"),
            ]),
            distribution => distribution.Phase == "measured");

        Assert.Equal(2, measured.IterationCount);
        Assert.Equal(11d, Metric(measured, "cpuPercent").Mean);
    }

    [Fact]
    public void Aggregate_counts_retry_attempts_within_an_iteration_once()
    {
        // A stage that retried once within iteration 1 emits two samples (attempt 1 and 2) but
        // must not be reported as two iterations.
        ResourceTelemetryDistribution distribution = Assert.Single(Aggregate(
        [
            Sample(1, 60d, 100d, 1d, attempt: 1),
            Sample(1, 10d, 100d, 1d, attempt: 2),
            Sample(2, 10d, 100d, 1d, attempt: 1),
        ]));

        Assert.Equal(2, distribution.IterationCount);
        Assert.Equal(3, Metric(distribution, "cpuPercent").SampleCount);
    }

    [Fact]
    public void Aggregate_counts_unavailable_iterations_separately_from_samples()
    {
        ResourceMetricStatistics vram = Metric(Assert.Single(Aggregate(
        [
            Sample(1, 10d, 100d, 1d, vram: (null, "no adapter")),
            Sample(2, 20d, 200d, 2d, vram: (9000, null)),
        ])), "availableVramMb");

        Assert.Equal(1, vram.SampleCount);
        Assert.Equal(1, vram.UnavailableSampleCount);
        Assert.Equal(9000d, vram.Maximum);
        // One iteration had no reading, so the verdict degrades even though the bound held.
        Assert.Equal(ResourceTelemetryStatus.Unavailable, vram.Status);
    }

    [Fact]
    public void Aggregate_ignores_non_finite_observations()
    {
        ResourceMetricStatistics cpu = Metric(Assert.Single(Aggregate(
        [
            Sample(1, 10d, 100d, 1d),
            Sample(2, double.NaN, 200d, 2d),
        ])), "cpuPercent");

        Assert.Equal(1, cpu.SampleCount);
        Assert.Equal(1, cpu.UnavailableSampleCount);
        Assert.Equal(10d, cpu.Maximum);
    }

    [Fact]
    public void Aggregate_is_deterministic_across_groupings()
    {
        BenchmarkStageResourceTelemetry[] samples = [Sample(1, 10d, 100d, 1d), Sample(2, 20d, 200d, 2d)];

        IReadOnlyList<ResourceTelemetryDistribution> first = Aggregate(samples);
        IReadOnlyList<ResourceTelemetryDistribution> second = Aggregate(samples.Reverse().ToArray());

        Assert.Equal(
            first.Select(d => (d.Stage, d.Phase)),
            second.Select(d => (d.Stage, d.Phase)));
    }

    [Fact]
    public void Aggregate_excludes_the_synthetic_stage_marker()
    {
        // Skipped runs record a placeholder check named "stage"; it is not a measured metric.
        var skipped = new BenchmarkStageResourceTelemetry
        {
            Stage = "audio-prep",
            Phase = "measured",
            ExecutionStatus = BenchmarkEvidenceStatus.Skipped,
            Validation = new ResourceTelemetryValidation
            {
                Status = ResourceTelemetryStatus.Skipped,
                Checks = [new ResourceTelemetryCheck("stage", ResourceTelemetryStatus.Skipped, null, null, "no stages ran")],
            },
        };

        Assert.Empty(Assert.Single(Aggregate([skipped])).Metrics);
    }

    [Fact]
    public void Aggregate_preserves_the_configured_threshold()
    {
        ResourceMetricStatistics cpu = Metric(Assert.Single(Aggregate([Sample(1, 10d, 100d, 1d, cpuThreshold: 50)])), "cpuPercent");

        Assert.Equal(50d, cpu.Threshold);
    }

    [Fact]
    public void Aggregate_counts_a_thresholdless_structural_failure()
    {
        // A structural failure (e.g. processor count changed between samples) reports
        // Threshold = null since it isn't a bound breach, but it must still count as a
        // failing sample rather than reporting Status = Failed with FailingSampleCount = 0.
        BenchmarkStageResourceTelemetry sample = new()
        {
            Stage = "audio-prep",
            Phase = "measured",
            Iteration = 1,
            Attempt = 1,
            ExecutionStatus = BenchmarkEvidenceStatus.Completed,
            Validation = new ResourceTelemetryValidation
            {
                Status = ResourceTelemetryStatus.Failed,
                Checks =
                [
                    new ResourceTelemetryCheck("cpuPercent", ResourceTelemetryStatus.Failed, null, null,
                        "Processor count changed between samples."),
                ],
            },
        };

        ResourceMetricStatistics cpu = Metric(Assert.Single(Aggregate([sample])), "cpuPercent");

        Assert.Equal(ResourceTelemetryStatus.Failed, cpu.Status);
        Assert.Equal(1, cpu.FailingSampleCount);
    }

    private static IReadOnlyList<ResourceTelemetryDistribution> Aggregate(
        IReadOnlyList<BenchmarkStageResourceTelemetry> samples) =>
        ResourceTelemetryAggregator.Aggregate(samples);

    private static ResourceMetricStatistics Metric(ResourceTelemetryDistribution distribution, string metric) =>
        Assert.Single(distribution.Metrics, candidate => candidate.Metric == metric);

    private static BenchmarkStageResourceTelemetry Sample(
        int iteration,
        double cpu,
        double workingSet,
        double allocated,
        string phase = "measured",
        (long? Value, string? Reason)? vram = null,
        double? cpuThreshold = 50,
        int attempt = 1) => new()
        {
            Stage = "audio-prep",
            Phase = phase,
            Iteration = iteration,
            Attempt = attempt,
            ExecutionStatus = BenchmarkEvidenceStatus.Completed,
            Validation = new ResourceTelemetryValidation
            {
                Status = ResourceTelemetryStatus.Passed,
                Checks =
            [
                Check("cpuPercent", cpu, cpuThreshold),
                Check("workingSetBytes", workingSet, 1000),
                Check("managedAllocatedBytes", allocated, 10),
                vram is null
                    ? Check("availableVramMb", 9000, 1000)
                    : new ResourceTelemetryCheck("availableVramMb", ResourceTelemetryStatus.Unavailable,
                        vram.Value.Value, 1000, vram.Value.Reason),
            ],
            },
        };

    private static ResourceTelemetryCheck Check(string metric, double? observed, double? threshold)
    {
        bool breached = threshold.HasValue && observed.HasValue && observed.Value > threshold.Value;
        return new ResourceTelemetryCheck(metric,
            breached ? ResourceTelemetryStatus.Failed : ResourceTelemetryStatus.Passed,
            observed, threshold, breached ? "breached" : null);
    }
}
