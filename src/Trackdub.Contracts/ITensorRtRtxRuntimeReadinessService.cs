using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;

namespace Trackdub.Contracts;

public interface ITensorRtRtxRuntimeReadinessService
{
    Task<TensorRtRtxRuntimeReadinessSnapshot> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public sealed record TensorRtRtxRuntimeReadinessSnapshot(
    bool IsSupportedPlatform,
    bool IsReady,
    string? ProviderId,
    string RouteDisplay,
    string StatusLabel,
    TensorRtRtxReadinessBlocker Blocker,
    string Detail,
    bool IsHardwareEligible,
    bool IsOrtProviderListed,
    bool IsRegisteredWithOrt,
    bool CanInstallWinMlProvider,
    string? InstallHint);
