namespace Trackdub.Application.Runtime;

/// <summary>
/// Outcome of a Windows ML catalog EP installation attempt for WinML catalog providers.
/// </summary>
public sealed record WinMlCatalogEpInstallResult(
    bool Succeeded,
    string? FailureDetail = null);

public interface IOpenVinoCatalogEpInstaller
{
    Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default);
}

public interface IQnnCatalogEpInstaller
{
    Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default);
}

public interface IVitisAiCatalogEpInstaller
{
    Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default);
}
