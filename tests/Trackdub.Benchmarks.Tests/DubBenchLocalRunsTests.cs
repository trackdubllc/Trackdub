using DubBench.ViewModels;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;

namespace Trackdub.Benchmarks.Tests;

public sealed class DubBenchLocalRunsTests
{
    [Fact]
    public void EmptyHistoryShowsHonestEmptyState()
    {
        var history = new FakeHistory([]);

        var vm = new LeaderboardTabViewModel(history);

        Assert.Empty(vm.Entries);
        Assert.Contains("No measured", vm.StatusMessage);
        Assert.Equal(BenchmarkEvidenceKind.Benchmark, history.RequestedKind);
    }

    [Fact]
    public void LocalHistoryShowsMeasuredDurationAndActualProvider()
    {
        var report = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "stage:asr",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ActualProvider = "CUDA",
            TimingsMilliseconds = new Dictionary<string, double?> { ["total"] = 123.45 }
        };
        var vm = new LeaderboardTabViewModel(new FakeHistory([report]));

        LocalBenchmarkEntry entry = Assert.Single(vm.Entries);
        Assert.Equal(report.RunId, entry.RunId);
        Assert.Equal("CUDA", entry.Provider);
        Assert.Equal("123 ms", entry.Duration);
        Assert.Equal("Completed", entry.Status);
    }

    private sealed class FakeHistory(IReadOnlyList<BenchmarkEvidenceReport> reports) : IBenchmarkEvidenceRepository
    {
        public BenchmarkEvidenceKind? RequestedKind { get; private set; }

        public Task SaveAsync(BenchmarkEvidenceReport report, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BenchmarkEvidenceReport?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BenchmarkEvidenceReport?>(reports.SingleOrDefault(x => x.RunId == runId));

        public Task<IReadOnlyList<BenchmarkEvidenceReport>> ListRecentAsync(
            BenchmarkEvidenceKind? kind, int limit, CancellationToken cancellationToken = default)
        {
            RequestedKind = kind;
            return Task.FromResult<IReadOnlyList<BenchmarkEvidenceReport>>(reports);
        }
    }
}
