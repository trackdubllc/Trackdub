namespace Trackdub.Contracts.StarterPacks;

public sealed record StarterPackImportResult(
    string PackId,
    bool Success,
    IReadOnlyList<string> Warnings,
    string? FailureReason = null);

public interface IStarterPackImportExportService
{
    Task<StarterPackImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task ExportAsync(string packId, string destinationPath, CancellationToken cancellationToken = default);

    Task ExportFromSettingsAsync(
        StudioSettings settings,
        string packId,
        string displayName,
        string description,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task DeleteUserPackAsync(string packId, CancellationToken cancellationToken = default);
}
