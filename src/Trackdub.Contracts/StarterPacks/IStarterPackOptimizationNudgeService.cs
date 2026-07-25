namespace Trackdub.Contracts.StarterPacks;

public interface IStarterPackOptimizationNudgeService
{
    Task<IReadOnlyList<StarterPackOptimizationNudge>> GetNudgesAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default);
}
