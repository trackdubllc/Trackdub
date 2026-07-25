using Trackdub.Contracts.Licensing;
using Trackdub.Domain;

namespace Trackdub.Application.Transcripts;

public enum RuntimeModelSetupDecision
{
    Cancel,
    Download,
    Import,
    SkipOptionalStage
}

public sealed record RuntimeModelSetupPrompt(
    RuntimeModelRequest Request,
    RequiredRuntimeModelStatus Status,
    bool CanSkipOptionalStage,
    bool IsRetry);

public sealed record RuntimeModelSetupResult(
    bool IsReady,
    IReadOnlyList<RuntimeStage> SkippedStages);

public sealed record RuntimeModelSetupCallbacks(
    Func<RuntimeModelSetupPrompt, Task<RuntimeModelSetupDecision>> ResolveDecisionAsync,
    Func<Task<string?>> PickImportFileAsync,
    Func<string, IProgress<ModelDownloadProgress>> CreateDownloadProgress,
    Func<Func<CancellationToken, Task>, string, Task> RunOperationAsync);

public static class RuntimeModelSetupWorkflow
{
    public static async Task<RuntimeModelSetupResult> EnsureModelsAvailableAsync(
        RuntimeModelWorkflow runtimeModels,
        IReadOnlyList<RuntimeModelRequest> requests,
        RuntimeModelSetupCallbacks callbacks,
        bool allowOptionalStageSkip = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeModels);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(callbacks);

        var skippedStages = new List<RuntimeStage>();
        foreach (RuntimeModelRequest request in requests)
        {
            RequiredRuntimeModelStatus? status = await runtimeModels
                .GetRequiredModelStatusAsync(request, cancellationToken);
            if (status is null || status.IsAvailable)
            {
                continue;
            }

            bool canSkip = allowOptionalStageSkip && IsOptionalRuntimeStage(request.Stage);
            bool isRetry = false;
            while (status is not null && !status.IsAvailable)
            {
                RuntimeModelSetupDecision decision = await callbacks
                    .ResolveDecisionAsync(new RuntimeModelSetupPrompt(request, status, canSkip, isRetry));

                if (decision is RuntimeModelSetupDecision.SkipOptionalStage && canSkip)
                {
                    skippedStages.Add(request.Stage);
                    break;
                }

                if (decision is RuntimeModelSetupDecision.Cancel)
                {
                    return new RuntimeModelSetupResult(IsReady: false, skippedStages);
                }

                RequiredRuntimeModelStatus? attemptedStatus = null;
                bool resolved = false;
                if (decision is RuntimeModelSetupDecision.Download && status.CanAutoDownload)
                {
                    (resolved, attemptedStatus) = await DownloadRequiredModelAsync(runtimeModels, request, status, callbacks);
                }
                else if (decision is RuntimeModelSetupDecision.Import && status.CanImportSingleFile)
                {
                    (resolved, attemptedStatus) = await ImportRequiredModelAsync(runtimeModels, request, status, callbacks);
                }

                if (resolved)
                {
                    break;
                }

                status = attemptedStatus is { IsAvailable: false }
                    ? attemptedStatus
                    : await runtimeModels.GetRequiredModelStatusAsync(request, cancellationToken) ?? status;
                isRetry = true;
            }
        }

        return new RuntimeModelSetupResult(IsReady: true, skippedStages);
    }

    public static async Task<RuntimeModelSetupResult> EnsureManifestCompanionModelsAvailableAsync(
        RuntimeModelWorkflow runtimeModels,
        IReadOnlyList<string> manifestAliases,
        RuntimeStage owningStage,
        RuntimeModelSetupCallbacks callbacks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeModels);
        ArgumentNullException.ThrowIfNull(manifestAliases);
        ArgumentNullException.ThrowIfNull(callbacks);

        var skippedStages = new List<RuntimeStage>();
        foreach (string manifestAlias in manifestAliases)
        {
            RequiredRuntimeModelStatus? status = await runtimeModels
                .GetManifestCompanionModelStatusAsync(manifestAlias, owningStage, cancellationToken)
                .ConfigureAwait(false);
            if (status is null || status.IsAvailable)
            {
                continue;
            }

            bool isRetry = false;
            while (status is not null && !status.IsAvailable)
            {
                var syntheticRequest = new RuntimeModelRequest(owningStage, PreferredModelAlias: manifestAlias);
                RuntimeModelSetupDecision decision = await callbacks
                    .ResolveDecisionAsync(new RuntimeModelSetupPrompt(syntheticRequest, status, CanSkipOptionalStage: false, isRetry))
                    .ConfigureAwait(false);

                if (decision is RuntimeModelSetupDecision.Cancel)
                {
                    return new RuntimeModelSetupResult(IsReady: false, skippedStages);
                }

                RequiredRuntimeModelStatus? attemptedStatus = null;
                bool resolved = false;
                if (decision is RuntimeModelSetupDecision.Download && status.CanAutoDownload)
                {
                    (resolved, attemptedStatus) = await DownloadManifestCompanionModelAsync(
                        runtimeModels,
                        manifestAlias,
                        owningStage,
                        status,
                        callbacks).ConfigureAwait(false);
                }

                if (resolved)
                {
                    break;
                }

                status = attemptedStatus is { IsAvailable: false }
                    ? attemptedStatus
                    : await runtimeModels.GetManifestCompanionModelStatusAsync(manifestAlias, owningStage, cancellationToken)
                        .ConfigureAwait(false) ?? status;
                isRetry = true;
            }
        }

        return new RuntimeModelSetupResult(IsReady: true, skippedStages);
    }

    private static async Task<(bool Resolved, RequiredRuntimeModelStatus? Status)> DownloadManifestCompanionModelAsync(
        RuntimeModelWorkflow runtimeModels,
        string manifestAlias,
        RuntimeStage owningStage,
        RequiredRuntimeModelStatus status,
        RuntimeModelSetupCallbacks callbacks)
    {
        bool downloaded = false;
        RequiredRuntimeModelStatus? updatedStatus = null;
        IProgress<ModelDownloadProgress> progress = callbacks.CreateDownloadProgress(status.StageDisplayName);
        await callbacks.RunOperationAsync(async cancellationToken =>
        {
            RequiredRuntimeModelStatus downloadStatus = await runtimeModels
                .DownloadManifestCompanionModelAsync(manifestAlias, owningStage, progress, cancellationToken)
                .ConfigureAwait(false);
            updatedStatus = downloadStatus;
            RequiredRuntimeModelStatus? remainingStatus = await runtimeModels
                .GetManifestCompanionModelStatusAsync(manifestAlias, owningStage, cancellationToken)
                .ConfigureAwait(false);
            downloaded = downloadStatus.IsAvailable || remainingStatus is null || remainingStatus.IsAvailable;
        }, $"Downloading {status.StageDisplayName.ToLowerInvariant()} model...").ConfigureAwait(false);

        return (downloaded, updatedStatus);
    }

    private static async Task<(bool Resolved, RequiredRuntimeModelStatus? Status)> DownloadRequiredModelAsync(
        RuntimeModelWorkflow runtimeModels,
        RuntimeModelRequest request,
        RequiredRuntimeModelStatus status,
        RuntimeModelSetupCallbacks callbacks)
    {
        bool downloaded = false;
        RequiredRuntimeModelStatus? updatedStatus = null;
        IProgress<ModelDownloadProgress> progress = callbacks.CreateDownloadProgress(status.StageDisplayName);
        await callbacks.RunOperationAsync(async cancellationToken =>
        {
            RequiredRuntimeModelStatus downloadStatus = await runtimeModels
                .DownloadRequiredModelAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
            updatedStatus = downloadStatus;
            RequiredRuntimeModelStatus? remainingStatus = await runtimeModels
                .GetRequiredModelStatusAsync(request, cancellationToken)
                .ConfigureAwait(false);
            downloaded = downloadStatus.IsAvailable || remainingStatus is null || remainingStatus.IsAvailable;
        }, $"Downloading {status.StageDisplayName.ToLowerInvariant()} model...");

        return (downloaded, updatedStatus);
    }

    private static async Task<(bool Resolved, RequiredRuntimeModelStatus? Status)> ImportRequiredModelAsync(
        RuntimeModelWorkflow runtimeModels,
        RuntimeModelRequest request,
        RequiredRuntimeModelStatus status,
        RuntimeModelSetupCallbacks callbacks)
    {
        string? sourceModelPath = await callbacks.PickImportFileAsync();
        if (string.IsNullOrWhiteSpace(sourceModelPath))
        {
            return (false, null);
        }

        bool imported = false;
        RequiredRuntimeModelStatus? updatedStatus = null;
        await callbacks.RunOperationAsync(async cancellationToken =>
        {
            RequiredRuntimeModelStatus importStatus = await runtimeModels
                .ImportRequiredModelAsync(request, sourceModelPath, cancellationToken)
                .ConfigureAwait(false);
            updatedStatus = importStatus;
            RequiredRuntimeModelStatus? remainingStatus = await runtimeModels
                .GetRequiredModelStatusAsync(request, cancellationToken)
                .ConfigureAwait(false);
            imported = importStatus.IsAvailable || remainingStatus is null || remainingStatus.IsAvailable;
        }, $"Importing {status.StageDisplayName.ToLowerInvariant()} model...");

        return (imported, updatedStatus);
    }

    private static bool IsOptionalRuntimeStage(RuntimeStage stage) =>
        stage is RuntimeStage.Separation;
}
