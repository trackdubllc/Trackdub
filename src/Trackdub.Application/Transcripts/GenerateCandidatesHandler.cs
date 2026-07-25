using Trackdub.Application.Artifacts;
using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed record GenerateCandidatesRequest(
    Guid ProjectId,
    Guid VoiceAssignmentId,
    Guid SpeakerId,
    Guid MediaAssetId,
    Guid TranslatedSegmentId,
    int SegmentIndex,
    string SegmentText,
    string TargetLanguage,
    int CandidateCount = 3);

public sealed record GenerateCandidatesResult(
    TtsCandidateGroup Group,
    IReadOnlyList<TtsTake> Candidates);

public sealed class GenerateCandidatesHandler(
    ITtsEngine ttsEngine,
    IVoiceCatalog voiceCatalog,
    IArtifactStore artifactStore,
    IFileFingerprintService fileFingerprintService,
    IMediaAssetRepository mediaAssetRepository,
    ITtsTakeRepository ttsTakeRepository,
    ITtsCandidateGroupRepository candidateGroupRepository)
{
    private const int MinCandidates = 1;
    private const int MaxCandidates = 5;

    private readonly ITtsEngine ttsEngine = ttsEngine ?? throw new ArgumentNullException(nameof(ttsEngine));
    private readonly IVoiceCatalog voiceCatalog = voiceCatalog ?? throw new ArgumentNullException(nameof(voiceCatalog));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly IFileFingerprintService fileFingerprintService = fileFingerprintService ?? throw new ArgumentNullException(nameof(fileFingerprintService));
    private readonly IMediaAssetRepository mediaAssetRepository = mediaAssetRepository ?? throw new ArgumentNullException(nameof(mediaAssetRepository));
    private readonly ITtsTakeRepository ttsTakeRepository = ttsTakeRepository ?? throw new ArgumentNullException(nameof(ttsTakeRepository));
    private readonly ITtsCandidateGroupRepository candidateGroupRepository = candidateGroupRepository ?? throw new ArgumentNullException(nameof(candidateGroupRepository));

    public async Task<GenerateCandidatesResult> HandleAsync(
        GenerateCandidatesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        int count = Math.Clamp(request.CandidateCount, MinCandidates, MaxCandidates);
        string textHash = TtsTextHash.Compute(request.SegmentIndex, request.SegmentText);

        // Check if an existing group exists for this segment so we can reuse its ID
        TtsCandidateGroup? existingGroup = await candidateGroupRepository
            .GetBySegmentAsync(request.TranslatedSegmentId, cancellationToken)
            .ConfigureAwait(false);
        Guid groupId = existingGroup?.Id ?? Guid.NewGuid();

        IReadOnlyList<VoiceCatalogEntry> voicePool = voiceCatalog.GetVoices(request.TargetLanguage);
        if (voicePool.Count == 0)
        {
            voicePool = voiceCatalog.GetVoices();
        }

        if (voicePool.Count == 0)
        {
            throw new InvalidOperationException("No voices available in the catalog to generate candidates.");
        }

        var takes = new List<TtsTake>(count);
        for (int i = 0; i < count; i++)
        {
            VoiceCatalogEntry voice = voicePool[i % voicePool.Count];
            Guid artifactId = Guid.NewGuid();
            string relativePath = ProjectArtifactPaths.GetTtsCandidateRelativePath(
                request.SpeakerId,
                request.TranslatedSegmentId,
                groupId,
                i,
                artifactId);

            TtsSynthesisResult result = await ttsEngine.SynthesizeAsync(
                new TtsSynthesisRequest(
                    request.SegmentText,
                    request.TargetLanguage,
                    voice,
                    Options: InferenceRequestOptions.Default),
                cancellationToken).ConfigureAwait(false);

            await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(tx.TemporaryPath)!);
            await File.WriteAllBytesAsync(tx.TemporaryPath, result.WavBytes, cancellationToken)
                .ConfigureAwait(false);
            await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);

            string finalPath = artifactStore.GetPath(relativePath);
            FileFingerprint fingerprint = await fileFingerprintService
                .ComputeAsync(finalPath, cancellationToken)
                .ConfigureAwait(false);

            double? durationSeconds = result.SampleRate > 0
                ? (double)result.DurationSamples / result.SampleRate
                : null;

            var artifact = new ProjectArtifact(
                artifactId,
                request.ProjectId,
                request.MediaAssetId,
                ArtifactKind.TtsTake,
                relativePath,
                fingerprint.Sha256,
                fingerprint.SizeBytes,
                durationSeconds,
                result.SampleRate,
                ChannelCount: 1,
                DateTimeOffset.UtcNow,
                StageRunId: null,
                $"tts-candidate:{result.ModelId}:{result.VoiceId}");

            TtsTake take = TtsTake.CreateStock(
                    request.ProjectId,
                    request.VoiceAssignmentId,
                    request.TranslatedSegmentId,
                    request.SegmentIndex,
                    textHash)
                .Complete(
                    artifact.Id,
                    stageRunId: null,
                    result.DurationSamples,
                    result.SampleRate,
                    result.Provider,
                    result.ModelId,
                    result.VoiceId,
                    durationOverrunRatio: null) with
            {
                CandidateGroupId = groupId,
                CandidateIndex = i,
                Variant = TtsCandidateVariant.Candidate
            };

            await mediaAssetRepository.SaveArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
            takes.Add(take);
        }

        // Save new takes first so there is never a window where the segment has no valid takes.
        foreach (TtsTake take in takes)
        {
            await ttsTakeRepository.SaveAsync(take, cancellationToken).ConfigureAwait(false);
        }

        // Mark previous takes stale only after the new ones are persisted.
        if (existingGroup is not null)
        {
            IReadOnlyList<TtsTake> existingTakes = await ttsTakeRepository
                .GetBySegmentAsync(request.TranslatedSegmentId, cancellationToken)
                .ConfigureAwait(false);
            foreach (TtsTake existingTake in existingTakes.Where(take =>
                         take.CandidateGroupId == groupId &&
                         take.Variant == TtsCandidateVariant.Candidate &&
                         !take.IsStale &&
                         !takes.Any(newTake => newTake.Id == take.Id)))
            {
                await ttsTakeRepository.SaveAsync(existingTake.MarkStale(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Upsert the candidate group — preserve the current selection if the selected take id is still in the new batch.
        TtsCandidateGroup group;
        if (existingGroup is not null)
        {
            bool selectionStillValid = takes.Any(t => t.Id == existingGroup.SelectedCandidateId);
            Guid selectedId = selectionStillValid ? existingGroup.SelectedCandidateId : takes[0].Id;
            group = existingGroup with { SelectedCandidateId = selectedId };
        }
        else
        {
            group = TtsCandidateGroup.Create(
                request.ProjectId,
                request.TranslatedSegmentId,
                request.SegmentIndex,
                takes[0].Id) with
            {
                Id = groupId
            };
        }

        await candidateGroupRepository.SaveAsync(group, cancellationToken).ConfigureAwait(false);
        return new GenerateCandidatesResult(group, takes);
    }
}
