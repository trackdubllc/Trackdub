using Trackdub.Contracts.Licensing;
using Trackdub.Domain;

namespace Trackdub.Contracts;

public sealed record ModelStateChange(
    string ModelId,
    ModelCacheState PreviousState,
    ModelCacheState NewState,
    DateTimeOffset Timestamp,
    string? FailureReason = null);

public sealed record ModelDownloadResult(
    string ModelId,
    bool Success,
    ModelCacheState NewState,
    string? FailureReason,
    bool Cancelled = false);

/// <summary>
/// Orchestrates model downloads with state transitions and notifications.
/// Also provides repair and uninstall actions.
/// </summary>
public interface IModelDownloadOrchestrator
{
    Task<ModelDownloadResult> DownloadAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModelDownloadResult> DownloadAsync(
        string modelId,
        string? variantAlias,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<ModelDownloadResult> RepairAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> UninstallAsync(string modelId, CancellationToken cancellationToken = default);

    IObservable<ModelStateChange> StateChanges { get; }
}
