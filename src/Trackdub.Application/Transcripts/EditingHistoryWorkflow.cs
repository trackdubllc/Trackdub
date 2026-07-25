namespace Trackdub.Application.Transcripts;

public sealed class EditingHistoryWorkflow(
    TranscriptProjectStateService stateService,
    SpeakerAssignmentService speakerAssignmentService,
    VoiceAssignmentService voiceAssignmentService,
    SegmentEditingService segmentEditingService,
    TranslationOrchestrationService translationOrchestrationService)
{
    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly SpeakerAssignmentService speakerAssignmentService = speakerAssignmentService ?? throw new ArgumentNullException(nameof(speakerAssignmentService));
    private readonly VoiceAssignmentService voiceAssignmentService = voiceAssignmentService ?? throw new ArgumentNullException(nameof(voiceAssignmentService));
    private readonly SegmentEditingService segmentEditingService = segmentEditingService ?? throw new ArgumentNullException(nameof(segmentEditingService));
    private readonly TranslationOrchestrationService translationOrchestrationService = translationOrchestrationService ?? throw new ArgumentNullException(nameof(translationOrchestrationService));

    public async Task<TranscriptProjectState> RestoreEditingStateAsync(
        RestoreEditingStateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        Guid projectId = currentState.ProjectState.Project.Id;

        foreach (var speaker in currentState.Speakers)
        {
            if (request.SpeakerDisplayNames.TryGetValue(speaker.Id, out string? displayName) &&
                !string.IsNullOrWhiteSpace(displayName) &&
                !string.Equals(speaker.DisplayName, displayName.Trim(), StringComparison.Ordinal))
            {
                await speakerAssignmentService.RenameSpeakerAsync(
                    currentState,
                    new RenameSpeakerRequest(speaker.Id, displayName),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await voiceAssignmentService.RestoreVoiceAssignmentsAsync(projectId, request.VoiceAssignments, cancellationToken).ConfigureAwait(false);

        if (request.TranscriptSegments.Count > 0)
        {
            await segmentEditingService.SaveTranscriptRevisionAsync(
                currentState,
                request.TranscriptSegments,
                "history-restore",
                cancellationToken).ConfigureAwait(false);
            currentState = await ReloadAsync(request.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
        }

        if (request.SelectedTranslationTargetLanguage is not null &&
            request.TranslatedSegments is { Count: > 0 } translatedSegments &&
            currentState.CurrentTranscriptRevision is not null)
        {
            await translationOrchestrationService.RestoreTranslationRevisionAsync(
                currentState,
                request.SelectedTranslationTargetLanguage,
                translatedSegments,
                cancellationToken).ConfigureAwait(false);
        }

        return await ReloadAsync(request.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    private Task<TranscriptProjectState> OpenAsync(CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage: null, cancellationToken);

    private Task<TranscriptProjectState> ReloadAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage, cancellationToken);
}
