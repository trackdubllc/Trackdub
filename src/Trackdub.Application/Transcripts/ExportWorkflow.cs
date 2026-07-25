namespace Trackdub.Application.Transcripts;

public sealed class ExportWorkflow(
    TranscriptProjectStateService stateService,
    ExportStageHandler exportStageHandler)
{
    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly ExportStageHandler exportStageHandler = exportStageHandler ?? throw new ArgumentNullException(nameof(exportStageHandler));

    public Task<ExportStageResult> ExportAsync(
        TranscriptProjectState currentState,
        ExportStageRequest request,
        CancellationToken cancellationToken) =>
        exportStageHandler.ExportAsync(currentState, request, cancellationToken);

    public async Task<TranscriptProjectState> ReloadAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        await stateService.OpenAsync(requestedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
}
