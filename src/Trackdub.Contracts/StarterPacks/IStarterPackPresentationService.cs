namespace Trackdub.Contracts.StarterPacks;

public interface IStarterPackPresentationService
{
    Task<IReadOnlyList<StarterPackSummary>> ListSummariesAsync(CancellationToken cancellationToken = default);

    Task<StarterPackSummary> GetSummaryAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default);

    Task<string?> GetRecommendedPackIdAsync(CancellationToken cancellationToken = default);

    Task<bool> RequiresVoiceCloningConsentAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetRunnablePackIdsAsync(CancellationToken cancellationToken = default);
}
