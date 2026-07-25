using Trackdub.Contracts.Licensing;
using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.Application.Transcripts;

public sealed class RuntimeModelWorkflow(IRuntimeModelBootstrapService? bootstrapService = null)
{
    public Task<RequiredRuntimeModelStatus?> GetRequiredModelStatusAsync(
        RuntimeModelRequest request,
        CancellationToken cancellationToken = default)
    {
        if (IsCloudTranslationRequest(request))
        {
            return Task.FromResult<RequiredRuntimeModelStatus?>(null);
        }

        if (bootstrapService is null)
        {
            return Task.FromResult<RequiredRuntimeModelStatus?>(null);
        }

        return bootstrapService.GetRequiredModelStatusAsync(request, cancellationToken);
    }

    public Task<RequiredRuntimeModelStatus> DownloadRequiredModelAsync(
        RuntimeModelRequest request,
        IProgress<ModelDownloadProgress>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (bootstrapService is null)
        {
            throw new InvalidOperationException("Runtime model download is not configured.");
        }

        return bootstrapService.DownloadRequiredModelAsync(request, downloadProgress, cancellationToken);
    }

    public Task<RequiredRuntimeModelStatus> ImportRequiredModelAsync(
        RuntimeModelRequest request,
        string sourceModelPath,
        CancellationToken cancellationToken = default)
    {
        if (bootstrapService is null)
        {
            throw new InvalidOperationException("Runtime model import is not configured.");
        }

        return bootstrapService.ImportRequiredModelAsync(request, sourceModelPath, cancellationToken);
    }

    public Task<RequiredRuntimeModelStatus?> GetManifestCompanionModelStatusAsync(
        string manifestAlias,
        RuntimeStage owningStage,
        CancellationToken cancellationToken = default)
    {
        if (bootstrapService is null)
        {
            return Task.FromResult<RequiredRuntimeModelStatus?>(null);
        }

        return bootstrapService.GetManifestCompanionModelStatusAsync(manifestAlias, owningStage, cancellationToken);
    }

    public Task<RequiredRuntimeModelStatus> DownloadManifestCompanionModelAsync(
        string manifestAlias,
        RuntimeStage owningStage,
        IProgress<ModelDownloadProgress>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (bootstrapService is null)
        {
            throw new InvalidOperationException("Runtime model download is not configured.");
        }

        return bootstrapService.DownloadManifestCompanionModelAsync(
            manifestAlias,
            owningStage,
            downloadProgress,
            cancellationToken);
    }

    private static bool IsCloudTranslationRequest(RuntimeModelRequest request) =>
        request.Stage is RuntimeStage.Translation &&
        TranslationModelOverrideSettings.IsDeepLModelAlias(request.PreferredModelAlias);
}
