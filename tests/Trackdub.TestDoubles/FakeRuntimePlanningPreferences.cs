using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.TestDoubles;

public sealed class FakeRuntimePlanningPreferences : IRuntimePlanningPreferences
{
    public string? PreferredModelTier { get; init; }

    public string? BenchmarkEvidenceId { get; init; }

    public Task<string?> GetPreferredModelTierAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PreferredModelTier);
    }

    public Task<string?> GetBenchmarkEvidenceIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BenchmarkEvidenceId);
    }
}
