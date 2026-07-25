using Trackdub.Contracts.Licensing;

namespace Trackdub.Contracts.StarterPacks;

/// <summary>
/// Shared entry point for CLI and Avalonia starter-pack flows (PR1: list, download, apply).
/// Extended in later PRs (compatibility, import/export, nudges).
/// </summary>
public interface IStarterPackCoordinator
{
    Task<IReadOnlyList<StarterPackSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<StarterPackSummary> GetSummaryAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default);

    Task<string?> GetRecommendedPackIdAsync(CancellationToken cancellationToken = default);

    Task<bool> RequiresVoiceCloningConsentAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default);

    Task<StarterPackDownloadResult> DownloadAsync(
        string packId,
        string profileId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<StarterPackApplyResult> ApplyAsync(
        string packId,
        string profileId,
        bool acceptVoiceCloningConsent,
        CancellationToken cancellationToken = default);

    Task<StarterPackCompatibilityReport> EvaluateCompatibilityAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default);

    Task<StarterPackImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task ExportAsync(string packId, string destinationPath, CancellationToken cancellationToken = default);

    Task DeleteUserPackAsync(string packId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StarterPackOptimizationNudge>> GetOptimizationNudgesAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default);
}
