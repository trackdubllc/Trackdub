using Trackdub.Contracts;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed class TtsDubPreviewWorkflow(
    TranscriptProjectStateService stateService,
    TtsDubPreviewCoordinator coordinator)
{
    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly TtsDubPreviewCoordinator coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public async Task<AudioPreviewSnapshot> PlayTakeAsync(int segmentIndex, CancellationToken ct)
    {
        TranscriptProjectState currentState = await stateService.OpenAsync(null, ct).ConfigureAwait(false);

        TtsSegmentState? state = currentState.TtsSegmentStates
            .FirstOrDefault(s => s.SegmentIndex == segmentIndex &&
                                 s.Status == TtsTakeStatus.Completed &&
                                 !s.IsStale &&
                                 !string.IsNullOrWhiteSpace(s.ArtifactRelativePath));

        if (state is null)
        {
            await coordinator.StopAsync(ct).ConfigureAwait(false);
            return AudioPreviewSnapshot.Empty with
            {
                WarningMessage = "No completed TTS take is available for this segment."
            };
        }

        await coordinator.OpenTakeAsync(state.ArtifactRelativePath!, ct).ConfigureAwait(false);

        AudioPreviewSnapshot afterOpen = await coordinator.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (!afterOpen.IsLoaded)
        {
            return afterOpen;
        }

        await coordinator.PlayAsync(ct).ConfigureAwait(false);
        return await coordinator.GetSnapshotAsync(ct).ConfigureAwait(false);
    }

    public async Task<AudioPreviewSnapshot> PlaySequenceAsync(CancellationToken ct)
    {
        TranscriptProjectState currentState = await stateService.OpenAsync(null, ct).ConfigureAwait(false);

        await coordinator.OpenSequenceAsync(currentState.TtsSegmentStates, ct).ConfigureAwait(false);

        AudioPreviewSnapshot afterOpen = await coordinator.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (!afterOpen.IsLoaded)
        {
            return afterOpen;
        }

        await coordinator.PlayAsync(ct).ConfigureAwait(false);
        return await coordinator.GetSnapshotAsync(ct).ConfigureAwait(false);
    }

    public Task<AudioPreviewSnapshot> PlayAmbianceAsync(CancellationToken ct)
    {
        return Task.FromResult(AudioPreviewSnapshot.Empty with
        {
            WarningMessage = "Ambiance preview requires stem separation, which is not yet available."
        });
    }

    public Task SwitchCandidateAsync(Guid translatedSegmentId, Guid candidateId, CancellationToken ct) =>
        coordinator.SwitchCandidateAsync(translatedSegmentId, candidateId, ct);

    public Task ReloadCurrentSegmentAsync(Guid translatedSegmentId, CancellationToken ct) =>
        coordinator.ReloadCurrentSegmentAsync(translatedSegmentId, ct);

    public Task PauseAsync(CancellationToken ct) => coordinator.PauseAsync(ct);

    public async Task<AudioPreviewSnapshot> StopAsync(CancellationToken ct)
    {
        await coordinator.StopAsync(ct).ConfigureAwait(false);
        return await coordinator.GetSnapshotAsync(ct).ConfigureAwait(false);
    }

    public Task<AudioPreviewSnapshot> GetSnapshotAsync(CancellationToken ct) =>
        coordinator.GetSnapshotAsync(ct);
}
