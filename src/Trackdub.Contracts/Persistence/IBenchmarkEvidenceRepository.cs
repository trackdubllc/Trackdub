using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Contracts.Persistence;

public interface IBenchmarkEvidenceRepository
{
    Task SaveAsync(BenchmarkEvidenceReport report, CancellationToken cancellationToken = default);

    Task<BenchmarkEvidenceReport?> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BenchmarkEvidenceReport>> ListRecentAsync(
        BenchmarkEvidenceKind? kind, int limit, CancellationToken cancellationToken = default);
}
