namespace Trackdub.Application.Transcripts;

using Trackdub.Contracts.Pipeline;

public sealed class TranslationWorkflow(
    TranscriptProjectStateService stateService,
    TranslationOrchestrationService translationOrchestrationService)
{
    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly TranslationOrchestrationService translationOrchestrationService = translationOrchestrationService ?? throw new ArgumentNullException(nameof(translationOrchestrationService));

    public async Task<TranscriptProjectState> SelectTranslationTargetAsync(
        SetTranslationTargetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await translationOrchestrationService.SetSelectedTranslationTargetLanguageAsync(
            currentState,
            request.TargetLanguage,
            cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(request.TargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> SetTranscriptLanguageAsync(
        SetTranscriptLanguageRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await translationOrchestrationService.SetTranscriptLanguageAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        string? selectedTranslationTargetLanguage =
            request.SelectedTranslationTargetLanguage ?? currentState.SelectedTranslationTargetLanguage;
        return await ReloadAsync(selectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> GenerateTranslationAsync(
        GenerateTranslationRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await translationOrchestrationService
            .GenerateTranslationAsync(currentState, request, cancellationToken, progress)
            .ConfigureAwait(false);
        return await ReloadAsync(
            TranscriptWorkflowUtilities.NormalizeTranslationTargetLanguageCode(request.TargetLanguage),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RetranslateSegmentAsync(
        RetranslateSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await translationOrchestrationService.RetranslateSegmentAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(
            TranscriptWorkflowUtilities.NormalizeTranslationTargetLanguageCode(request.TargetLanguage),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> SaveTranslationEditsAsync(
        SaveTranslationEditsRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await translationOrchestrationService.SaveTranslationEditsAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(
            TranscriptWorkflowUtilities.NormalizeTranslationTargetLanguageCode(request.TargetLanguage),
            cancellationToken).ConfigureAwait(false);
    }

    private Task<TranscriptProjectState> OpenAsync(CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage: null, cancellationToken);

    private Task<TranscriptProjectState> ReloadAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage, cancellationToken);
}
