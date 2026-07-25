using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class StageRunHygieneTests
{
    [Fact]
    public async Task ReconcileStaleRunningAsync_ages_old_running_rows_to_failed()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        StageRunRecord stale = StageRunRecord.Start(projectId, StageNames.Vad, DateTimeOffset.UtcNow.AddMinutes(-45));
        await store.CreateAsync(stale, CancellationToken.None);

        IReadOnlyList<StageRunRecord> reconciled = await StageRunHygiene.ReconcileStaleRunningAsync(
            store,
            [stale],
            logger: null,
            CancellationToken.None);

        StageRunRecord updated = Assert.Single(reconciled);
        Assert.Equal(StageRunStatus.Failed, updated.Status);
        Assert.Equal(StageRunHygiene.StaleRunningFailureReason, updated.FailureReason);
        StageRunRecord persisted = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Failed, persisted.Status);
    }

    [Fact]
    public async Task ReconcileStaleRunningAsync_leaves_recent_running_rows_unchanged()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        StageRunRecord recent = StageRunRecord.Start(projectId, StageNames.Asr, DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.CreateAsync(recent, CancellationToken.None);

        IReadOnlyList<StageRunRecord> reconciled = await StageRunHygiene.ReconcileStaleRunningAsync(
            store,
            [recent],
            logger: null,
            CancellationToken.None);

        StageRunRecord unchanged = Assert.Single(reconciled);
        Assert.Equal(StageRunStatus.Running, unchanged.Status);
        Assert.Equal(0, store.UpdateCallCount);
    }

    [Fact]
    public async Task ReconcileStaleRunningAsync_preserves_runs_in_preserveRunIds()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        StageRunRecord stale = StageRunRecord.Start(projectId, StageNames.Vad, DateTimeOffset.UtcNow.AddMinutes(-45));
        await store.CreateAsync(stale, CancellationToken.None);

        var preserveIds = new HashSet<Guid> { stale.Id };
        IReadOnlyList<StageRunRecord> reconciled = await StageRunHygiene.ReconcileStaleRunningAsync(
            store,
            [stale],
            logger: null,
            CancellationToken.None,
            preserveRunIds: preserveIds);

        StageRunRecord preserved = Assert.Single(reconciled);
        Assert.Equal(StageRunStatus.Running, preserved.Status);
        Assert.Equal(0, store.UpdateCallCount);
    }
}

public sealed class StageArtifactResumeEvaluatorTests
{
    [Fact]
    public void CanResumeStage_returns_false_when_model_alias_mismatch()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Vad, DateTimeOffset.UtcNow.AddHours(-1))
            .WithRuntimeInfo("cpu", "cpu", modelAlias: "whisper-small")
            .Complete(DateTimeOffset.UtcNow);

        TranscriptProjectState state = CreateState(projectId, [run], artifacts: []);
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"Model:{StageNames.Vad}"] = "whisper-large"
        };

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Vad,
            snapshot,
            projectRootPath: "."));
    }

    [Fact]
    public void CanResumeStage_returns_false_when_model_variant_mismatch()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Translation, DateTimeOffset.UtcNow.AddHours(-1))
            .WithRuntimeInfo("cpu", "cpu", modelAlias: "phi-4-mini", modelVariant: "cpu-int4")
            .Complete(DateTimeOffset.UtcNow);

        TranscriptProjectState state = CreateState(projectId, [run], artifacts: []);
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"Model:{StageNames.Translation}"] = "phi-4-mini",
            [$"ModelVariant:{StageNames.Translation}"] = "gpu-int4",
        };

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Translation,
            snapshot,
            projectRootPath: "."));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_completed_vad_with_speech_regions_artifact()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Vad, DateTimeOffset.UtcNow.AddHours(-1))
            .Complete(DateTimeOffset.UtcNow);

        const string regionsPath = "artifacts/speech-regions/regions.json";
        artifactStore.Seed(regionsPath);
        ProjectArtifact regionsArtifact = new(
            Guid.NewGuid(),
            projectId,
            Guid.NewGuid(),
            ArtifactKind.SpeechRegions,
            regionsPath,
            "hash",
            2,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            StageRunId: run.Id);

        TranscriptProjectState state = CreateState(projectId, [run], [regionsArtifact]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Vad,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_completed_overlap_rescue_with_metadata_on_disk()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.OverlapRescue, DateTimeOffset.UtcNow.AddHours(-1))
            .Complete(DateTimeOffset.UtcNow);

        const string metadataPath = "artifacts/overlap-rescue/run/region-0/metadata.json";
        artifactStore.Seed(metadataPath);
        ProjectArtifact metadataArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.OverlapRescueMetadata,
            metadataPath,
            "hash",
            64,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            StageRunId: run.Id);

        TranscriptProjectState state = CreateState(projectId, [run], [metadataArtifact]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.OverlapRescue,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_overlap_rescue_when_metadata_missing_on_disk()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.OverlapRescue, DateTimeOffset.UtcNow.AddHours(-1))
            .Complete(DateTimeOffset.UtcNow);

        const string metadataPath = "artifacts/overlap-rescue/run/region-0/metadata.json";
        ProjectArtifact metadataArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.OverlapRescueMetadata,
            metadataPath,
            "hash",
            64,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            StageRunId: run.Id);

        TranscriptProjectState state = CreateState(projectId, [run], [metadataArtifact]);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.OverlapRescue,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_completed_lip_sync_with_take_artifacts_on_disk()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.LipSync, now.AddHours(-1))
            .Complete(now);

        const string lipSyncPath = "artifacts/lip-sync/run/segment-0.wav";
        artifactStore.Seed(lipSyncPath);
        ProjectArtifact lipSyncArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.LipSyncTake,
            lipSyncPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id,
            Provenance: "lipsync:take:00000000-0000-0000-0000-000000000001");

        TranscriptProjectState state = CreateState(projectId, [run], [lipSyncArtifact]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.LipSync,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_partially_completed_lip_sync_even_with_artifacts()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.LipSync, now.AddHours(-1))
            .PartiallyComplete(now, "Some segments failed.");

        const string lipSyncPath = "artifacts/lip-sync/run/segment-0.wav";
        artifactStore.Seed(lipSyncPath);
        ProjectArtifact lipSyncArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.LipSyncTake,
            lipSyncPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id);

        TranscriptProjectState state = CreateState(projectId, [run], [lipSyncArtifact]);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.LipSync,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_partially_completed_diarization_even_with_turns()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Diarization, now.AddHours(-1))
            .PartiallyComplete(now, "Some speakers failed.");

        const string diarizationPath = "artifacts/diarization/run/result.json";
        artifactStore.Seed(diarizationPath);
        ProjectArtifact diarizationArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.DiarizationResult,
            diarizationPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id);

        SpeakerTurn turn = SpeakerTurn.Create(projectId, speakerId, 0.0, 2.0, stageRunId: run.Id);
        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [diarizationArtifact],
            speakerTurns: [turn]);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Diarization,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_completed_diarization_with_artifact_on_disk()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Diarization, now.AddHours(-1))
            .Complete(now);

        const string diarizationPath = "artifacts/diarization/run/result.json";
        artifactStore.Seed(diarizationPath);
        ProjectArtifact diarizationArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.DiarizationResult,
            diarizationPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id);

        SpeakerTurn turn = SpeakerTurn.Create(projectId, speakerId, 0.0, 2.0, stageRunId: run.Id);
        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [diarizationArtifact],
            speakerTurns: [turn]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Diarization,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_partially_completed_asr_even_with_segments()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-1))
            .PartiallyComplete(now, "Some regions failed.");

        const string rawAsrPath = "artifacts/asr/run/raw.json";
        artifactStore.Seed(rawAsrPath);
        ProjectArtifact rawArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            rawAsrPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id,
            Provenance: "generated-asr-raw");

        var revision = TranscriptRevision.Create(projectId, run.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [rawArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment]);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Asr,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_completed_asr_with_raw_artifact_on_disk()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-1))
            .Complete(now);

        const string rawAsrPath = "artifacts/asr/run/raw.json";
        artifactStore.Seed(rawAsrPath);
        ProjectArtifact rawArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            rawAsrPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id,
            Provenance: "generated-asr-raw");

        var revision = TranscriptRevision.Create(projectId, run.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [rawArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Asr,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_partially_completed_speaker_assignment_even_with_segments()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord asrRun = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-2))
            .Complete(now.AddHours(-2));
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.SpeakerAssignment, now.AddHours(-1))
            .PartiallyComplete(now, "Persistence interrupted.");

        const string transcriptPath = "artifacts/transcript/transcript-revision-0001.json";
        artifactStore.Seed(transcriptPath);
        ProjectArtifact transcriptArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            transcriptPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: asrRun.Id,
            Provenance: "generated-asr");

        var revision = TranscriptRevision.Create(projectId, asrRun.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [asrRun, run],
            [transcriptArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment]);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.SpeakerAssignment,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_completed_speaker_assignment_with_transcript_artifact_on_disk()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord asrRun = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-2))
            .Complete(now.AddHours(-2));
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.SpeakerAssignment, now.AddHours(-1))
            .Complete(now);

        const string transcriptPath = "artifacts/transcript/transcript-revision-0001.json";
        artifactStore.Seed(transcriptPath);
        ProjectArtifact transcriptArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            transcriptPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: asrRun.Id,
            Provenance: "generated-asr");

        var revision = TranscriptRevision.Create(projectId, asrRun.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [asrRun, run],
            [transcriptArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.SpeakerAssignment,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_speaker_assignment_when_transcript_artifact_missing_on_disk()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord asrRun = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-2))
            .Complete(now.AddHours(-2));
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.SpeakerAssignment, now.AddHours(-1))
            .Complete(now);

        const string transcriptPath = "artifacts/transcript/transcript-revision-0001.json";
        ProjectArtifact transcriptArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            transcriptPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: asrRun.Id,
            Provenance: "generated-asr");

        var revision = TranscriptRevision.Create(projectId, asrRun.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [asrRun, run],
            [transcriptArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment]);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.SpeakerAssignment,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_speaker_assignment_when_only_raw_asr_artifact_present()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord asrRun = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-2))
            .Complete(now.AddHours(-2));
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.SpeakerAssignment, now.AddHours(-1))
            .Complete(now);

        const string rawAsrPath = "artifacts/transcript/raw-asr-00000000000000000000000000000001.json";
        artifactStore.Seed(rawAsrPath);
        ProjectArtifact rawArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            rawAsrPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: asrRun.Id,
            Provenance: "generated-asr-raw");

        var revision = TranscriptRevision.Create(projectId, asrRun.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [asrRun, run],
            [rawArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment]);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.SpeakerAssignment,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_speaker_assignment_when_asr_model_mismatch()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord asrRun = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-2))
            .WithRuntimeInfo("cpu", "cpu", modelAlias: "whisper-small")
            .Complete(now.AddHours(-2));
        StageRunRecord speakerRun = StageRunRecord
            .Start(projectId, StageNames.SpeakerAssignment, now.AddHours(-1))
            .Complete(now);

        const string transcriptPath = "artifacts/transcript/transcript-revision-0001.json";
        artifactStore.Seed(transcriptPath);
        ProjectArtifact transcriptArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            transcriptPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: asrRun.Id,
            Provenance: "generated-asr");

        var revision = TranscriptRevision.Create(projectId, asrRun.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [asrRun, speakerRun],
            [transcriptArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment]);

        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"Model:{StageNames.Asr}"] = "whisper-large"
        };

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.SpeakerAssignment,
            snapshot,
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_asr_when_explicit_source_language_mismatch()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-1))
            .Complete(now);

        const string rawAsrPath = "artifacts/asr/run/raw.json";
        artifactStore.Seed(rawAsrPath);
        ProjectArtifact rawArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            rawAsrPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id,
            Provenance: "generated-asr-raw");

        var revision = TranscriptRevision.Create(projectId, run.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello", detectedLanguage: "en");

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [rawArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment],
            transcriptLanguage: "en");

        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SourceLanguage"] = "es"
        };

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Asr,
            snapshot,
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_completed_audio_preparation_with_analysis_artifact()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.AudioPreparation, now.AddHours(-1))
            .Complete(now);

        const string analysisPath = "artifacts/audio-quality/run/analysis.json";
        artifactStore.Seed(analysisPath);
        ProjectArtifact analysisArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.AudioQualityAnalysis,
            analysisPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id);

        TranscriptProjectState state = CreateState(projectId, [run], [analysisArtifact]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.AudioPreparation,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_completed_speech_enhancement_with_enhanced_audio()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.SpeechEnhancement, now.AddHours(-1))
            .Complete(now);

        const string enhancedPath = "artifacts/speech-enhanced/run/enhanced.wav";
        artifactStore.Seed(enhancedPath);
        ProjectArtifact enhancedArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.SpeechEnhancedAudio,
            enhancedPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id);

        TranscriptProjectState state = CreateState(projectId, [run], [enhancedArtifact]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.SpeechEnhancement,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_tts_when_only_one_of_two_segments_has_take()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid translationRevisionId = Guid.NewGuid();
        Guid voiceAssignmentId = Guid.NewGuid();
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Tts, now.AddHours(-1))
            .Complete(now);

        Guid translatedSegmentId = Guid.NewGuid();
        Guid artifactId = Guid.NewGuid();
        const string takePath = "artifacts/tts/run/segment-0.wav";
        artifactStore.Seed(takePath);
        ProjectArtifact ttsArtifact = new(
            artifactId,
            projectId,
            mediaAssetId,
            ArtifactKind.TtsTake,
            takePath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id);

        TtsTake take = TtsTake
            .CreateStock(projectId, voiceAssignmentId, translatedSegmentId, segmentIndex: 0)
            .Complete(artifactId, run.Id, durationSamples: 24000, sampleRate: 24000, provider: "test", modelId: null, voiceId: null, durationOverrunRatio: null);

        TranslationRevision translationRevision = TranslationRevision.Create(
            projectId,
            run.Id,
            Guid.NewGuid(),
            "es",
            revisionNumber: 1,
            now);
        translationRevision = translationRevision with { Id = translationRevisionId };

        IReadOnlyList<TranslatedSegment> translatedSegments =
        [
            TranslatedSegment.Create(translationRevisionId, 0, 0, 1.5, "hola"),
            TranslatedSegment.Create(translationRevisionId, 1, 1.5, 3.0, "mundo")
        ];

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [ttsArtifact],
            currentTranslationRevision: translationRevision,
            translatedSegments: translatedSegments,
            ttsTakes: [take]);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Tts,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_tts_when_all_translated_segments_have_completed_takes()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid translationRevisionId = Guid.NewGuid();
        Guid voiceAssignmentId = Guid.NewGuid();
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Tts, now.AddHours(-1))
            .Complete(now);

        Guid artifactId0 = Guid.NewGuid();
        Guid artifactId1 = Guid.NewGuid();
        const string takePath0 = "artifacts/tts/run/segment-0.wav";
        const string takePath1 = "artifacts/tts/run/segment-1.wav";
        artifactStore.Seed(takePath0);
        artifactStore.Seed(takePath1);

        ProjectArtifact ttsArtifact0 = new(
            artifactId0,
            projectId,
            mediaAssetId,
            ArtifactKind.TtsTake,
            takePath0,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id);
        ProjectArtifact ttsArtifact1 = new(
            artifactId1,
            projectId,
            mediaAssetId,
            ArtifactKind.TtsTake,
            takePath1,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id);

        TtsTake take0 = TtsTake
            .CreateStock(projectId, voiceAssignmentId, Guid.NewGuid(), segmentIndex: 0)
            .Complete(artifactId0, run.Id, durationSamples: 24000, sampleRate: 24000, provider: "test", modelId: null, voiceId: null, durationOverrunRatio: null);
        TtsTake take1 = TtsTake
            .CreateStock(projectId, voiceAssignmentId, Guid.NewGuid(), segmentIndex: 1)
            .Complete(artifactId1, run.Id, durationSamples: 24000, sampleRate: 24000, provider: "test", modelId: null, voiceId: null, durationOverrunRatio: null);

        TranslationRevision translationRevision = TranslationRevision.Create(
            projectId,
            run.Id,
            Guid.NewGuid(),
            "es",
            revisionNumber: 1,
            now);
        translationRevision = translationRevision with { Id = translationRevisionId };

        IReadOnlyList<TranslatedSegment> translatedSegments =
        [
            TranslatedSegment.Create(translationRevisionId, 0, 0, 1.5, "hola"),
            TranslatedSegment.Create(translationRevisionId, 1, 1.5, 3.0, "mundo")
        ];

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [ttsArtifact0, ttsArtifact1],
            currentTranslationRevision: translationRevision,
            translatedSegments: translatedSegments,
            ttsTakes: [take0, take1]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Tts,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    private static TranscriptProjectState CreateState(
        Guid projectId,
        IReadOnlyList<StageRunRecord> stageRuns,
        IReadOnlyList<ProjectArtifact>? artifacts = null,
        TranslationRevision? currentTranslationRevision = null,
        IReadOnlyList<TranslatedSegment>? translatedSegments = null,
        IReadOnlyList<TtsTake>? ttsTakes = null,
        IReadOnlyList<SpeakerTurn>? speakerTurns = null,
        TranscriptRevision? currentTranscriptRevision = null,
        IReadOnlyList<TranscriptSegment>? transcriptSegments = null,
        string transcriptLanguage = "en")
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "test", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            projectId,
            "media/source.mp4",
            "source.mp4",
            "abc123",
            1024L,
            now,
            "mp4",
            DurationSeconds: 30.0,
            HasAudio: true,
            HasVideo: true,
            now);
        var openResult = new OpenProjectResult(
            project,
            mediaAsset,
            SourceReference: null,
            SourceMediaStatus.Available,
            SourceStatusMessage: null,
            Artifacts: artifacts ?? [],
            TranscriptLanguage: transcriptLanguage);

        return new TranscriptProjectState(
            openResult,
            CurrentTranscriptRevision: currentTranscriptRevision,
            TranscriptSegments: transcriptSegments ?? [],
            Speakers: [],
            SpeakerTurns: speakerTurns ?? [],
            CurrentTranslationRevision: currentTranslationRevision,
            TranslatedSegments: translatedSegments ?? [],
            IsTranslationStale: false,
            TranscriptLanguage: transcriptLanguage,
            StageRuns: stageRuns,
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: null,
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: [],
            VoiceAssignments: [],
            TtsTakes: ttsTakes ?? [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);
    }

    [Fact]
    public void RuntimeMatchesSnapshot_returns_true_when_alias_variant_and_modelId_all_match()
    {
        StageRunRecord run = StageRunRecord
            .Start(Guid.NewGuid(), StageNames.Asr, DateTimeOffset.UtcNow.AddHours(-1))
            .WithRuntimeInfo("auto", "dml", modelId: "whisper-large-v3-onnx", modelAlias: "whisper-large-v3", modelVariant: "fp16")
            .Complete(DateTimeOffset.UtcNow);
        var snapshot = new Dictionary<string, string>
        {
            [$"Model:{StageNames.Asr}"] = "whisper-large-v3",
            [$"ModelVariant:{StageNames.Asr}"] = "fp16",
            [$"ModelId:{StageNames.Asr}"] = "whisper-large-v3-onnx",
        };

        Assert.True(StageArtifactResumeEvaluator.RuntimeMatchesSnapshot(run, StageNames.Asr, snapshot));
    }

    [Fact]
    public void RuntimeMatchesSnapshot_returns_false_when_modelId_mismatches_even_if_alias_matches()
    {
        // Same alias, different underlying model build/weights (e.g. manifest updated under the same alias).
        StageRunRecord run = StageRunRecord
            .Start(Guid.NewGuid(), StageNames.Asr, DateTimeOffset.UtcNow.AddHours(-1))
            .WithRuntimeInfo("auto", "dml", modelId: "whisper-large-v3-onnx-v1", modelAlias: "whisper-large-v3")
            .Complete(DateTimeOffset.UtcNow);
        var snapshot = new Dictionary<string, string>
        {
            [$"Model:{StageNames.Asr}"] = "whisper-large-v3",
            [$"ModelId:{StageNames.Asr}"] = "whisper-large-v3-onnx-v2",
        };

        Assert.False(StageArtifactResumeEvaluator.RuntimeMatchesSnapshot(run, StageNames.Asr, snapshot));
    }

    [Fact]
    public void RuntimeMatchesSnapshot_returns_false_when_modelId_expected_but_run_has_none_recorded()
    {
        StageRunRecord run = StageRunRecord
            .Start(Guid.NewGuid(), StageNames.Asr, DateTimeOffset.UtcNow.AddHours(-1))
            .WithRuntimeInfo("auto", "dml", modelAlias: "whisper-large-v3")
            .Complete(DateTimeOffset.UtcNow);
        var snapshot = new Dictionary<string, string> { [$"ModelId:{StageNames.Asr}"] = "whisper-large-v3-onnx" };

        Assert.False(StageArtifactResumeEvaluator.RuntimeMatchesSnapshot(run, StageNames.Asr, snapshot));
    }

    [Fact]
    public void RuntimeMatchesSnapshot_skips_modelId_check_when_snapshot_key_absent()
    {
        // No "ModelId:{stage}" key at all (current production snapshot builders only populate it when
        // an IModelAliasResolver is wired) - absence must not force a mismatch, so callers without the
        // resolver wired stay backward compatible.
        StageRunRecord run = StageRunRecord
            .Start(Guid.NewGuid(), StageNames.Asr, DateTimeOffset.UtcNow.AddHours(-1))
            .WithRuntimeInfo("auto", "dml", modelAlias: "whisper-large-v3", modelVariant: "fp16")
            .Complete(DateTimeOffset.UtcNow);
        var snapshot = new Dictionary<string, string>
        {
            [$"Model:{StageNames.Asr}"] = "whisper-large-v3",
            [$"ModelVariant:{StageNames.Asr}"] = "fp16",
        };

        Assert.True(StageArtifactResumeEvaluator.RuntimeMatchesSnapshot(run, StageNames.Asr, snapshot));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_asr_when_requested_source_language_does_not_match_persisted_transcript_language()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-1))
            .Complete(now);

        const string rawAsrPath = "artifacts/asr/run/raw.json";
        artifactStore.Seed(rawAsrPath);
        ProjectArtifact rawArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            rawAsrPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id,
            Provenance: "generated-asr-raw");

        var revision = TranscriptRevision.Create(projectId, run.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [rawArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment],
            transcriptLanguage: "en");

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Asr,
            snapshot: new Dictionary<string, string> { ["SourceLanguage"] = "es" },
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_asr_when_requested_source_language_matches_persisted_transcript_language()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-1))
            .Complete(now);

        const string rawAsrPath = "artifacts/asr/run/raw.json";
        artifactStore.Seed(rawAsrPath);
        ProjectArtifact rawArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            rawAsrPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: run.Id,
            Provenance: "generated-asr-raw");

        var revision = TranscriptRevision.Create(projectId, run.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [rawArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment],
            transcriptLanguage: "en");

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Asr,
            snapshot: new Dictionary<string, string> { ["SourceLanguage"] = "en" },
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_speaker_assignment_when_upstream_asr_alias_mismatches_snapshot()
    {
        // SpeakerAssignment's own run/outputs are all valid; only the upstream ASR run's recorded
        // model alias disagrees with what the snapshot now expects. AsrUpstreamMatchesSnapshot must
        // catch this even though RuntimeMatchesSnapshot(speakerAssignmentRun, ...) trivially passes.
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord asrRun = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-2))
            .WithRuntimeInfo("cpu", "cpu", modelAlias: "whisper-small")
            .Complete(now.AddHours(-2));
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.SpeakerAssignment, now.AddHours(-1))
            .Complete(now);

        const string transcriptPath = "artifacts/transcript/transcript-revision-0001.json";
        artifactStore.Seed(transcriptPath);
        ProjectArtifact transcriptArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            transcriptPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: asrRun.Id,
            Provenance: "generated-asr");

        var revision = TranscriptRevision.Create(projectId, asrRun.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [asrRun, run],
            [transcriptArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment]);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.SpeakerAssignment,
            snapshot: new Dictionary<string, string> { [$"Model:{StageNames.Asr}"] = "whisper-large" },
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_speaker_assignment_when_upstream_asr_alias_matches_snapshot()
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord asrRun = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-2))
            .WithRuntimeInfo("cpu", "cpu", modelAlias: "whisper-small")
            .Complete(now.AddHours(-2));
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.SpeakerAssignment, now.AddHours(-1))
            .Complete(now);

        const string transcriptPath = "artifacts/transcript/transcript-revision-0001.json";
        artifactStore.Seed(transcriptPath);
        ProjectArtifact transcriptArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            transcriptPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: asrRun.Id,
            Provenance: "generated-asr");

        var revision = TranscriptRevision.Create(projectId, asrRun.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 2.0, "hello");

        TranscriptProjectState state = CreateState(
            projectId,
            [asrRun, run],
            [transcriptArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment]);

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.SpeakerAssignment,
            snapshot: new Dictionary<string, string> { [$"Model:{StageNames.Asr}"] = "whisper-small" },
            projectRootPath: artifactStore.GetPath(".")));
    }
}
