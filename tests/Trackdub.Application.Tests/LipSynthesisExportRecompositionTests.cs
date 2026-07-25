using Trackdub.Application.LipSynthesis;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class LipSynthesisExportRecompositionTests
{
    [Fact]
    public void TryBuildResolvedPlan_maps_latest_stage_run_takes_to_speaker_turns()
    {
        var artifactStore = new FakeArtifactStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid turnId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord lipRun = StageRunRecord.Start(projectId, StageNames.LipSynthesis, now).Complete(now);

        const string relativeClip = "artifacts/lipsynthesis/turn.mp4";
        artifactStore.Seed(relativeClip, [0, 0, 0, 12]);
        string absoluteClip = artifactStore.GetPath(relativeClip);

        var lipArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.LipSynthesisTake,
            relativeClip,
            "hash",
            12,
            2.0d,
            null,
            null,
            now,
            StageRunId: lipRun.Id,
            Provenance: $"lipsynthesis:turn:{turnId:N}");

        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            @"C:\media\source.mp4",
            "source.mp4",
            "hash",
            100,
            now,
            "mp4",
            10.0d,
            HasAudio: true,
            HasVideo: true,
            now);

        TranscriptProjectState state = CreateState(
            projectId,
            mediaAsset,
            [lipArtifact],
            [lipRun],
            [SpeakerTurn.Create(projectId, speakerId, 1.0, 3.0) with { Id = turnId }]);

        var plan = LipSynthesisExportRecomposition.TryBuildResolvedPlan(
            state,
            artifactStore,
            mediaAsset.SourceFilePath);

        Assert.NotNull(plan);
        Assert.Single(plan.PatchedTurns);
        Assert.Equal(TimeSpan.FromSeconds(1), plan.PatchedTurns[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(3), plan.PatchedTurns[0].End);
        Assert.Equal(absoluteClip, plan.PatchedTurns[0].PatchedClipPath);
    }

    [Fact]
    public void BuildExportCompositingWarning_returns_null_when_plan_is_composable()
    {
        var artifactStore = new FakeArtifactStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid turnId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord lipRun = StageRunRecord.Start(projectId, StageNames.LipSynthesis, now).Complete(now);

        const string relativeClip = "artifacts/lipsynthesis/turn.mp4";
        artifactStore.Seed(relativeClip, [0, 0, 0, 12]);
        string absoluteClip = artifactStore.GetPath(relativeClip);

        var lipArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.LipSynthesisTake,
            relativeClip,
            "hash",
            12,
            2.0d,
            null,
            null,
            now,
            StageRunId: lipRun.Id,
            Provenance: $"lipsynthesis:turn:{turnId:N}");

        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            @"C:\media\source.mp4",
            "source.mp4",
            "hash",
            100,
            now,
            "mp4",
            10.0d,
            HasAudio: true,
            HasVideo: true,
            now);

        TranscriptProjectState state = CreateState(
            projectId,
            mediaAsset,
            [lipArtifact],
            [lipRun],
            [SpeakerTurn.Create(projectId, speakerId, 1.0, 3.0) with { Id = turnId }]);

        string? warning = LipSynthesisExportRecomposition.BuildExportCompositingWarning(state, artifactStore);

        Assert.Null(warning);
    }

    private static TranscriptProjectState CreateState(
        Guid projectId,
        MediaAsset mediaAsset,
        IReadOnlyList<ProjectArtifact> artifacts,
        IReadOnlyList<StageRunRecord> stageRuns,
        IReadOnlyList<SpeakerTurn> speakerTurns)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "Test", now, now);
        var projectState = new OpenProjectResult(
            project,
            mediaAsset,
            null,
            SourceMediaStatus.Available,
            null,
            artifacts,
            "en");

        return new TranscriptProjectState(
            projectState,
            CurrentTranscriptRevision: null,
            TranscriptSegments: [],
            Speakers: [],
            SpeakerTurns: speakerTurns,
            CurrentTranslationRevision: null,
            TranslatedSegments: [],
            IsTranslationStale: false,
            TranscriptLanguage: "en",
            StageRuns: stageRuns,
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: null,
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: [],
            VoiceAssignments: [],
            TtsTakes: [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);
    }
}
