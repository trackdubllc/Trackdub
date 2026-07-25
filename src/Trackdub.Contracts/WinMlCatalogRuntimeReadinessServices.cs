using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Contracts;

public sealed record WinMlCatalogRuntimeReadinessSnapshot(
    bool IsSupportedPlatform,
    bool IsReady,
    string? ProviderId,
    string RouteDisplay,
    string StatusLabel,
    WinMlCatalogReadinessBlocker Blocker,
    string Detail,
    bool IsHardwareEligible,
    bool IsOrtProviderListed,
    bool IsRegisteredWithOrt,
    bool CanInstallWinMlProvider,
    string? InstallHint);

public interface IOpenVinoCatalogRuntimeReadinessService
{
    Task<WinMlCatalogRuntimeReadinessSnapshot> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public interface IQnnCatalogRuntimeReadinessService
{
    Task<WinMlCatalogRuntimeReadinessSnapshot> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public interface IVitisAiCatalogRuntimeReadinessService
{
    Task<WinMlCatalogRuntimeReadinessSnapshot> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}
