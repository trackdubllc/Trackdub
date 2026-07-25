using Trackdub.Domain;

namespace Trackdub.Contracts.Licensing;

public sealed record RuntimeModelRequest(
    RuntimeStage Stage,
    string? PreferredModelAlias = null,
    string? SourceLanguage = null,
    string? TargetLanguage = null,
    bool RequirePreferredModelAlias = false,
    ExecutionProviderKind? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null);

public sealed record RequiredRuntimeModelStatus(
    RuntimeStage Stage,
    string StageDisplayName,
    string ModelId,
    string? ModelAlias,
    string? Variant,
    string ExpectedFileName,
    string ModelPath,
    string SourceUrl,
    string License,
    bool IsAvailable,
    bool CanAutoDownload,
    bool CanImportSingleFile,
    bool RequiresAttribution,
    bool RequiresUserConsent,
    string HelpText,
    string? FailureReason = null);

public interface IRuntimeModelBootstrapService
{
    Task<RequiredRuntimeModelStatus?> GetRequiredModelStatusAsync(
        RuntimeModelRequest request,
        CancellationToken cancellationToken = default);

    Task<RequiredRuntimeModelStatus> DownloadRequiredModelAsync(
        RuntimeModelRequest request,
        IProgress<ModelDownloadProgress>? downloadProgress = null,
        CancellationToken cancellationToken = default);

    Task<RequiredRuntimeModelStatus> ImportRequiredModelAsync(
        RuntimeModelRequest request,
        string sourceModelPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns download status for a manifest alias that is not resolved via <see cref="RuntimeStage"/> planning
    /// (for example InsightFace companions bundled with lip synthesis).
    /// </summary>
    Task<RequiredRuntimeModelStatus?> GetManifestCompanionModelStatusAsync(
        string manifestAlias,
        RuntimeStage owningStage,
        CancellationToken cancellationToken = default);

    Task<RequiredRuntimeModelStatus> DownloadManifestCompanionModelAsync(
        string manifestAlias,
        RuntimeStage owningStage,
        IProgress<ModelDownloadProgress>? downloadProgress = null,
        CancellationToken cancellationToken = default);
}
