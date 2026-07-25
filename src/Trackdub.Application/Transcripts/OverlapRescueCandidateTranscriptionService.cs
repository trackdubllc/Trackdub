using System.Text.Json;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Transcript;

namespace Trackdub.Application.Transcripts;

public sealed class OverlapRescueCandidateTranscriptionService(
    AsrStageHandler asrStageHandler,
    IArtifactStore artifactStore,
    IFileFingerprintService fileFingerprintService,
    IMediaAssetRepository mediaAssetRepository)
{
    public async Task TranscribeCandidatesAsync(
        TranscriptProjectState state,
        OverlapRescueStageResult rescueResult,
        InferenceModelPreferences? modelPreferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rescueResult);

        Guid projectId = state.ProjectState.Project.Id;
        MediaAsset mediaAsset = TranscriptWorkflowUtilities.GetRequiredMediaAsset(state);

        foreach (OverlapRescueRegionResult region in rescueResult.Regions)
        {
            for (int candidateIndex = 0; candidateIndex < 2; candidateIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectArtifact candidateArtifact = candidateIndex == 0
                    ? region.SourceCandidate0
                    : region.SourceCandidate1;

                string candidatePath = artifactStore.GetPath(candidateArtifact.RelativePath);
                SpeechRegion[] regions = [new SpeechRegion(0, 0d, candidateArtifact.DurationSeconds ?? 0d)];
                AsrStageResult asrResult = await asrStageHandler.HandleAsync(
                    new AsrStageRequest(
                        projectId,
                        candidatePath,
                        regions,
                        modelPreferences?.AsrModelAlias,
                        RequirePreferredModelAlias: false,
                        state.TranscriptLanguage,
                        modelPreferences?.GetPreferredExecutionProvider(RuntimeStage.Asr),
                        modelPreferences?.RequiresPreferredExecutionProvider(RuntimeStage.Asr) == true,
                        modelPreferences?.GetPreferredModelVariantAlias(RuntimeStage.Asr)),
                    cancellationToken).ConfigureAwait(false);

                RecognizedTranscriptSegment? recognized = asrResult.Segments.FirstOrDefault();
                var payload = new OverlapRescueCandidateTranscriptPayload(
                    region.RegionIndex,
                    candidateIndex,
                    region.StartSeconds,
                    region.EndSeconds,
                    recognized?.Text ?? string.Empty,
                    RequiresReview: true,
                    Source: $"overlap-rescue-candidate-{candidateIndex}",
                    AsrStageRunId: asrResult.StageRun.Id,
                    ParentOverlapRescueStageRunId: rescueResult.StageRun.Id);

                string relativePath = ProjectArtifactPaths.GetOverlapRescueCandidateTranscriptRelativePath(
                    rescueResult.StageRun.Id,
                    region.RegionIndex,
                    candidateIndex);

                await using ArtifactWriteHandle handle = artifactStore.CreateWriteHandle(relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(handle.TemporaryPath)!);
                await File.WriteAllTextAsync(
                    handle.TemporaryPath,
                    JsonSerializer.Serialize(payload, OverlapRescueJson.Options),
                    cancellationToken).ConfigureAwait(false);
                await artifactStore.CommitAsync(handle, cancellationToken).ConfigureAwait(false);

                FileFingerprint fingerprint = await fileFingerprintService
                    .ComputeAsync(artifactStore.GetPath(relativePath), cancellationToken)
                    .ConfigureAwait(false);

                var artifact = new ProjectArtifact(
                    Guid.NewGuid(),
                    projectId,
                    mediaAsset.Id,
                    ArtifactKind.OverlapRescueCandidateTranscript,
                    relativePath,
                    fingerprint.Sha256,
                    fingerprint.SizeBytes,
                    candidateArtifact.DurationSeconds,
                    candidateArtifact.SampleRate,
                    candidateArtifact.ChannelCount,
                    DateTimeOffset.UtcNow,
                    rescueResult.StageRun.Id,
                    $"source=overlap-rescue-candidate-{candidateIndex};requires_review=true;region_index={region.RegionIndex}");

                await mediaAssetRepository.SaveArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

internal sealed record OverlapRescueCandidateTranscriptPayload(
    int RegionIndex,
    int CandidateIndex,
    double RegionStartSeconds,
    double RegionEndSeconds,
    string Text,
    bool RequiresReview,
    string Source,
    Guid AsrStageRunId,
    Guid ParentOverlapRescueStageRunId);
