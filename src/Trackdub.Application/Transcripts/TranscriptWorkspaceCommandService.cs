using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;

namespace Trackdub.Application.Transcripts;

public sealed record PreviewMixCommandResult(
    PreviewMixStageResult Preview,
    TranscriptProjectState ProjectState);

public sealed record ExportCommandResult(
    ExportStageResult Export,
    TranscriptProjectState ProjectState);

public sealed class TranscriptWorkspaceCommandService
{
    public async Task<TranscriptProjectState> RunTranslationAsync(
        TranscriptWorkspace workspace,
        string sourceLanguageCode,
        string targetLanguageCode,
        CancellationToken cancellationToken,
        InferenceModelPreferences? modelPreferences = null,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        await workspace.SetTranscriptLanguageAsync(
            new SetTranscriptLanguageRequest(sourceLanguageCode, targetLanguageCode),
            cancellationToken).ConfigureAwait(false);

        return await workspace.GenerateTranslationAsync(
            new GenerateTranslationRequest(
                sourceLanguageCode,
                targetLanguageCode,
                modelPreferences?.TranslationModelAlias,
                modelPreferences?.GetPreferredExecutionProvider(RuntimeStage.Translation),
                modelPreferences?.RequiresPreferredExecutionProvider(RuntimeStage.Translation) == true,
                modelPreferences?.GetPreferredModelVariantAlias(RuntimeStage.Translation)),
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    public Task<TranscriptProjectState> SaveTranscriptAsync(
        TranscriptWorkspace workspace,
        SaveTranscriptEditsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);

        return workspace.SaveTranscriptEditsAsync(request, cancellationToken);
    }

    public async Task<PreviewMixCommandResult> CreatePreviewMixAsync(
        TranscriptWorkspace workspace,
        TranscriptProjectState projectState,
        PreviewMixStageRequest request,
        string? selectedTranslationTargetLanguageCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(projectState);
        ArgumentNullException.ThrowIfNull(request);

        return await workspace.RunPipelineAsync(
            nameof(CreatePreviewMixAsync),
            async ct =>
            {
                TranscriptProjectState latestState = await workspace.Project
                    .ReloadAsync(selectedTranslationTargetLanguageCode, ct)
                    .ConfigureAwait(false);
                PreviewMixStageResult preview = await workspace.Preview
                    .GeneratePreviewAsync(latestState, request, ct)
                    .ConfigureAwait(false);
                TranscriptProjectState refreshedState = await workspace.Project
                    .ReloadAsync(selectedTranslationTargetLanguageCode, ct)
                    .ConfigureAwait(false);
                return new PreviewMixCommandResult(preview, refreshedState);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportCommandResult> CreateExportAsync(
        TranscriptWorkspace workspace,
        TranscriptProjectState projectState,
        ExportStageRequest request,
        string? selectedTranslationTargetLanguageCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(projectState);
        ArgumentNullException.ThrowIfNull(request);

        return await workspace.RunPipelineAsync(
            nameof(CreateExportAsync),
            async ct =>
            {
                TranscriptProjectState latestState = await workspace.Project
                    .ReloadAsync(selectedTranslationTargetLanguageCode, ct)
                    .ConfigureAwait(false);
                ExportStageResult export = await workspace.Export
                    .ExportAsync(latestState, request, ct)
                    .ConfigureAwait(false);
                TranscriptProjectState refreshedState = await workspace.Export
                    .ReloadAsync(selectedTranslationTargetLanguageCode, ct)
                    .ConfigureAwait(false);
                return new ExportCommandResult(export, refreshedState);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
