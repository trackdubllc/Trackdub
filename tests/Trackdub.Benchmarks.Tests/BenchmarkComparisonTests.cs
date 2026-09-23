using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests;

public sealed class BenchmarkComparisonTests
{
    [Fact]
    public void CompareRejectsSkipsFallbacksAndMismatchedConditions()
    {
        BenchmarkEvidenceReport first = Sample(10);
        BenchmarkEvidenceReport second = Sample(30);
        BenchmarkEvidenceReport skipped = Sample(1) with { Status = BenchmarkEvidenceStatus.Skipped };
        BenchmarkEvidenceReport fallback = Sample(2) with { ActualProvider = "Cpu" };
        BenchmarkEvidenceReport differentFixture = Sample(3) with { FixtureSha256 = new string('b', 64) };
        BenchmarkEvidenceReport observation = Sample(4) with { Kind = BenchmarkEvidenceKind.Observation };

        BenchmarkComparisonResult comparison = BenchmarkComparison.Compare(
            [first, skipped, second, fallback, differentFixture, observation]);

        Assert.Equal(2, comparison.Accepted.Count);
        Assert.Equal(4, comparison.Rejected.Count);
        Assert.Equal(20, comparison.MedianMilliseconds);
        Assert.Equal(10, comparison.MinimumMilliseconds);
        Assert.Equal(30, comparison.MaximumMilliseconds);
    }

    [Fact]
    public void CompareRejectsUnavailableTimingAndPartialStage()
    {
        BenchmarkEvidenceReport unavailable = Sample(10) with
        {
            TimingsMilliseconds = new Dictionary<string, double?> { ["pipeline"] = null }
        };
        BenchmarkEvidenceReport partial = Sample(20) with
        {
            Stages = [new BenchmarkEvidenceStage
            {
                Name = "Asr",
                Status = BenchmarkEvidenceStatus.PartiallyCompleted,
            }]
        };

        BenchmarkComparisonResult comparison = BenchmarkComparison.Compare([unavailable, partial]);

        Assert.Empty(comparison.Accepted);
        Assert.Null(comparison.MedianMilliseconds);
    }

    [Fact]
    public void CompareRequiresProviderForLowercaseModelStage()
    {
        BenchmarkEvidenceReport missingProvider = Sample(10) with
        {
            Scenario = "asr",
            RequestedProvider = null,
            ActualProvider = null,
        };

        BenchmarkComparisonResult comparison = BenchmarkComparison.Compare([missingProvider]);

        Assert.Empty(comparison.Accepted);
        Assert.Contains(missingProvider.RunId, comparison.Rejected.Keys);
    }

    [Fact]
    public void CompareAcceptsEquivalentProviderSpelling()
    {
        BenchmarkEvidenceReport alias = Sample(10) with
        {
            RequestedProvider = "TensorRTRtx",
            ActualProvider = "tensor-rt-rtx",
        };

        BenchmarkComparisonResult comparison = BenchmarkComparison.Compare([alias]);

        Assert.Single(comparison.Accepted);
    }

    [Fact]
    public void CompareAcceptsDirectMlRequestAgainstDmlLabel()
    {
        BenchmarkEvidenceReport directMl = Sample(10) with
        {
            RequestedProvider = "DirectMl",
            ActualProvider = "dml",
        };

        BenchmarkComparisonResult comparison = BenchmarkComparison.Compare([directMl]);

        Assert.Single(comparison.Accepted);
    }

    [Fact]
    public void CompareAcceptsExecutionProviderSuffixedRequest()
    {
        BenchmarkEvidenceReport directMl = Sample(10) with
        {
            RequestedProvider = "CPUExecutionProvider",
            ActualProvider = "cpu",
        };

        BenchmarkComparisonResult comparison = BenchmarkComparison.Compare([directMl]);

        Assert.Single(comparison.Accepted);
    }

    private static BenchmarkEvidenceReport Sample(double milliseconds) => new()
    {
        RunId = Guid.NewGuid(),
        Kind = BenchmarkEvidenceKind.Benchmark,
        Scenario = "Asr",
        RunMode = "fresh-process-isolated-engine-cache",
        Status = BenchmarkEvidenceStatus.Completed,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        FixtureSha256 = new string('a', 64),
        RequestedProvider = "Cuda",
        ActualProvider = "Cuda",
        TimingsMilliseconds = new Dictionary<string, double?> { ["pipeline"] = milliseconds },
    };
}
