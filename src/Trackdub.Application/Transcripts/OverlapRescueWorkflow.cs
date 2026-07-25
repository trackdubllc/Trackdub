using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;

namespace Trackdub.Application.Transcripts;

public sealed class OverlapRescueWorkflow(
    OverlapRescueStageHandler overlapRescueStageHandler,
    OverlapRegionDetector overlapRegionDetector,
    OverlapRescueCandidateTranscriptionService? candidateTranscriptionService = null)
{
    private readonly OverlapRescueStageHandler overlapRescueStageHandler = overlapRescueStageHandler ?? throw new ArgumentNullException(nameof(overlapRescueStageHandler));
    private readonly OverlapRegionDetector overlapRegionDetector = overlapRegionDetector ?? throw new ArgumentNullException(nameof(overlapRegionDetector));

    public bool HasSuggestedOverlapRegions(TranscriptProjectState state) =>
        overlapRegionDetector
            .DetectFromSpeakerTurns(state.SpeakerTurns, ResolveMediaDuration(state))
            .Count > 0;

    public async Task<OverlapRescueStageResult> RunAsync(
        TranscriptProjectState state,
        CancellationToken cancellationToken,
        IProgress<OverlapRescueProgress>? progress = null,
        string? preferredModelAlias = null,
        InferenceModelPreferences? modelPreferences = null,
        bool retranscribeCandidates = false)
    {
        ArgumentNullException.ThrowIfNull(state);

        MediaAsset mediaAsset = TranscriptWorkflowUtilities.GetRequiredMediaAsset(state);
        ProjectArtifact sourceAudioArtifact = TranscriptWorkflowUtilities.ResolveAsrAudioArtifact(
            state.ProjectState.Artifacts,
            state.StageRuns)
            ?? throw new InvalidOperationException("No audio artifact is available for overlap speech rescue.");

        IReadOnlyList<OverlapRegion> regions = overlapRegionDetector.DetectFromSpeakerTurns(
            state.SpeakerTurns,
            mediaAsset.DurationSeconds);

        OverlapRescueStageResult result = await overlapRescueStageHandler.HandleAsync(
            new OverlapRescueStageRequest(
                state.ProjectState.Project.Id,
                mediaAsset,
                sourceAudioArtifact,
                regions,
                state.ProjectState.Artifacts,
                preferredModelAlias,
                modelPreferences?.GetPreferredExecutionProvider(RuntimeStage.OverlapRescue),
                modelPreferences?.RequiresPreferredExecutionProvider(RuntimeStage.OverlapRescue) == true,
                modelPreferences?.GetPreferredModelVariantAlias(RuntimeStage.OverlapRescue)),
            progress,
            cancellationToken).ConfigureAwait(false);

        if (retranscribeCandidates && candidateTranscriptionService is not null && result.Regions.Count > 0)
        {
            await candidateTranscriptionService
                .TranscribeCandidatesAsync(state, result, modelPreferences, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    private static double? ResolveMediaDuration(TranscriptProjectState state) =>
        TranscriptWorkflowUtilities.GetRequiredMediaAsset(state).DurationSeconds;
}
