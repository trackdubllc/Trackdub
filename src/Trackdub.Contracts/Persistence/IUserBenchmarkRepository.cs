using Trackdub.Domain;

namespace Trackdub.Contracts.Persistence;

public interface IUserBenchmarkRepository
{
    Task AddAsync(
        BenchmarkRunRecord run,
        CancellationToken cancellationToken = default);

    Task<bool> ContainsEvidenceAsync(
        Guid evidenceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BenchmarkRunRecord>> ListByEvidenceIdAsync(
        Guid evidenceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BenchmarkRunRecord>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
