namespace Trackdub.Application.Transcripts;

public sealed class TranscriptWorkflow(
    TranscriptProjectStateService stateService,
    SegmentEditingService segmentEditingService,
    TranscriptGenerationService transcriptGenerationService)
{
    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly SegmentEditingService segmentEditingService = segmentEditingService ?? throw new ArgumentNullException(nameof(segmentEditingService));
    private readonly TranscriptGenerationService transcriptGenerationService = transcriptGenerationService ?? throw new ArgumentNullException(nameof(transcriptGenerationService));

    public async Task<TranscriptProjectState> SaveEditsAsync(
        SaveTranscriptEditsRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await segmentEditingService.SaveEditsAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> SplitSegmentAsync(
        SplitTranscriptSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await segmentEditingService.SplitSegmentAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> MergeSegmentsAsync(
        MergeTranscriptSegmentsRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await segmentEditingService.MergeSegmentsAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> MergeSegmentRunAsync(
        MergeTranscriptSegmentRunRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await segmentEditingService.MergeSegmentRunAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> TrimSegmentAsync(
        TrimTranscriptSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await segmentEditingService.TrimSegmentAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> DeleteSegmentAsync(
        DeleteTranscriptSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await segmentEditingService.DeleteSegmentAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RetranscribeSegmentsAsync(
        RetranscribeTranscriptSegmentsRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await transcriptGenerationService.RetranscribeSegmentsAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    private Task<TranscriptProjectState> OpenAsync(CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage: null, cancellationToken);

    private Task<TranscriptProjectState> ReloadAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage, cancellationToken);
}
