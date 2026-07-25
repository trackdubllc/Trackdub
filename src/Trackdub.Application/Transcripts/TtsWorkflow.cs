namespace Trackdub.Application.Transcripts;

using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;

public sealed class TtsWorkflow(
    TranscriptProjectStateService stateService,
    TtsOrchestrationService ttsOrchestrationService,
    ISpeakerConsentService? speakerConsentService = null)
    : IDisposable
{
    private const string ConcurrentOperationMessage = "A TTS operation is already running for this project.";

    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly TtsOrchestrationService ttsOrchestrationService = ttsOrchestrationService ?? throw new ArgumentNullException(nameof(ttsOrchestrationService));
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public ISpeakerConsentService? SpeakerConsent { get; } = speakerConsentService;

    public void Dispose() => operationGate.Dispose();

    public async Task<TranscriptProjectState> GenerateTtsForSpeakerAsync(
        GenerateTtsForSpeakerRequest request,
        CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(
            async token =>
            {
                TranscriptProjectState currentState = await OpenAsync(token).ConfigureAwait(false);
                await ttsOrchestrationService.GenerateTtsForSpeakerAsync(currentState, request, token).ConfigureAwait(false);
                return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> GenerateTtsForSegmentAsync(
        GenerateTtsForSegmentRequest request,
        CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(
            async token =>
            {
                TranscriptProjectState currentState = await OpenAsync(token).ConfigureAwait(false);
                await ttsOrchestrationService.GenerateTtsForSegmentAsync(currentState, request, token).ConfigureAwait(false);
                return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> GenerateTtsForAllSpeakersAsync(
        GenerateTtsForAllSpeakersRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        return await RunExclusiveAsync(
            async token =>
            {
                TranscriptProjectState currentState = await OpenAsync(token).ConfigureAwait(false);
                await ttsOrchestrationService
                    .GenerateTtsForAllSpeakersAsync(currentState, request, token, progress)
                    .ConfigureAwait(false);
                return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RegenerateStaleTtsForSpeakerAsync(
        RegenerateStaleTtsForSpeakerRequest request,
        CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(
            async token =>
            {
                TranscriptProjectState currentState = await OpenAsync(token).ConfigureAwait(false);
                await ttsOrchestrationService.RegenerateStaleTtsForSpeakerAsync(currentState, request, token).ConfigureAwait(false);
                return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> StretchTtsTakeAsync(
        StretchTtsTakeRequest request,
        CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(
            async token =>
            {
                TranscriptProjectState currentState = await OpenAsync(token).ConfigureAwait(false);
                await ttsOrchestrationService.StretchTtsTakeAsync(currentState, request, token).ConfigureAwait(false);
                return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TranscriptProjectState> RunExclusiveAsync(
        Func<CancellationToken, Task<TranscriptProjectState>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!operationGate.Wait(0))
        {
            throw new InvalidOperationException(ConcurrentOperationMessage);
        }

        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private Task<TranscriptProjectState> OpenAsync(CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage: null, cancellationToken);

    private Task<TranscriptProjectState> ReloadAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage, cancellationToken);
}
