using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Composition.StarterPacks;

public sealed class StarterPackCoordinator(
    IStarterPackPresentationService presentation,
    IStarterPackDownloadService download,
    IStarterPackApplyService apply,
    IStarterPackCompatibilityService compatibility,
    IStarterPackImportExportService importExport,
    IStarterPackOptimizationNudgeService optimizationNudges) : IStarterPackCoordinator
{
    public Task<IReadOnlyList<StarterPackSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        presentation.ListSummariesAsync(cancellationToken);

    public Task<StarterPackSummary> GetSummaryAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default) =>
        presentation.GetSummaryAsync(packId, profileId, cancellationToken);

    public Task<string?> GetRecommendedPackIdAsync(CancellationToken cancellationToken = default) =>
        presentation.GetRecommendedPackIdAsync(cancellationToken);

    public Task<bool> RequiresVoiceCloningConsentAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default) =>
        presentation.RequiresVoiceCloningConsentAsync(packId, profileId, cancellationToken);

    public Task<StarterPackDownloadResult> DownloadAsync(
        string packId,
        string profileId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        download.DownloadAsync(packId, profileId, progress, cancellationToken);

    public Task<StarterPackApplyResult> ApplyAsync(
        string packId,
        string profileId,
        bool acceptVoiceCloningConsent,
        CancellationToken cancellationToken = default) =>
        apply.ApplyAsync(
            packId,
            profileId,
            hardwareProfile: null,
            acceptVoiceCloningConsent,
            cancellationToken);

    public Task<StarterPackCompatibilityReport> EvaluateCompatibilityAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default) =>
        compatibility.EvaluateAsync(packId, profileId, hardwareProfile: null, cancellationToken);

    public Task<StarterPackImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        importExport.ImportAsync(sourcePath, cancellationToken);

    public Task ExportAsync(string packId, string destinationPath, CancellationToken cancellationToken = default) =>
        importExport.ExportAsync(packId, destinationPath, cancellationToken);

    public Task DeleteUserPackAsync(string packId, CancellationToken cancellationToken = default) =>
        importExport.DeleteUserPackAsync(packId, cancellationToken);

    public Task<IReadOnlyList<StarterPackOptimizationNudge>> GetOptimizationNudgesAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default) =>
        optimizationNudges.GetNudgesAsync(packId, profileId, cancellationToken);
}
