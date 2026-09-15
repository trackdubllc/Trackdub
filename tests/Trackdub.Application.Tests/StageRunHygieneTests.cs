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
            .CreateStock(
                projectId,
                voiceAssignmentId,
                translatedSegmentId,
                segmentIndex: 0,
                translatedTextHash: TtsTextHash.Compute(0, "hola"))
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
            .CreateStock(
                projectId,
                voiceAssignmentId,
                Guid.NewGuid(),
                segmentIndex: 0,
                translatedTextHash: TtsTextHash.Compute(0, "hola"))
            .Complete(artifactId0, run.Id, durationSamples: 24000, sampleRate: 24000, provider: "test", modelId: null, voiceId: null, durationOverrunRatio: null);
        TtsTake take1 = TtsTake
            .CreateStock(
                projectId,
                voiceAssignmentId,
                Guid.NewGuid(),
                segmentIndex: 1,
                translatedTextHash: TtsTextHash.Compute(1, "mundo"))
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

    [Theory]
    [InlineData(SourceMediaStatus.Missing)]
    [InlineData(SourceMediaStatus.Changed)]
    public void CanResumeStage_returns_false_when_source_media_missing_or_changed(SourceMediaStatus status)
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

        // Source-dependent artifacts can never be resumed once the media is gone or replaced.
        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [regionsArtifact],
            sourceStatus: status);

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Vad,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_when_snapshot_source_path_differs_from_ingested_reference()
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

        var reference = new SourceMediaReference(
            @"D:\media\original.mp4",
            "original.mp4",
            new FileFingerprint("hash", 1024, DateTimeOffset.UtcNow),
            Probe: new MediaProbeSnapshot("mp4", "MP4", 30.0, BitRate: null, AudioStreams: [], VideoStreams: [], SubtitleStreams: []),
            DateTimeOffset.UtcNow);

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [regionsArtifact],
            sourceReference: reference);

        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SourceMediaPath"] = @"D:\media\replaced.mp4"
        };

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Vad,
            snapshot,
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_when_source_media_available_and_path_matches()
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

        // Build platform-native absolute paths: "." collapse is exercised everywhere, and the
        // case difference applies only where the evaluator compares OrdinalIgnoreCase
        // (Windows/macOS); Linux compares Ordinal.
        string mediaDir = OperatingSystem.IsWindows() ? @"D:\media" : "/media";
        var reference = new SourceMediaReference(
            Path.Combine(mediaDir, "source.mp4"),
            "source.mp4",
            new FileFingerprint("hash", 1024, DateTimeOffset.UtcNow),
            Probe: new MediaProbeSnapshot("mp4", "MP4", 30.0, BitRate: null, AudioStreams: [], VideoStreams: [], SubtitleStreams: []),
            DateTimeOffset.UtcNow);

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [regionsArtifact],
            sourceReference: reference);

        // Normalization and case differences on the same file must still match.
        string sourceMediaFileName = OperatingSystem.IsLinux() ? "source.mp4" : "SOURCE.MP4";
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SourceMediaPath"] = Path.Combine(
                mediaDir,
                Path.GetFileName(sourceMediaFileName))
        };

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Vad,
            snapshot,
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void RuntimeMatchesSnapshot_returns_false_when_provider_mismatch()
    {
        StageRunRecord run = StageRunRecord
            .Start(Guid.NewGuid(), StageNames.Asr, DateTimeOffset.UtcNow.AddHours(-1))
            .WithRuntimeInfo("cpu", "cpu", modelAlias: "whisper-large")
            .Complete(DateTimeOffset.UtcNow);
        var snapshot = new Dictionary<string, string>
        {
            [$"Provider:{StageNames.Asr}"] = "dml"
        };

        Assert.False(StageArtifactResumeEvaluator.RuntimeMatchesSnapshot(run, StageNames.Asr, snapshot));
    }

    [Fact]
    public void RuntimeMatchesSnapshot_returns_true_when_provider_matches()
    {
        StageRunRecord run = StageRunRecord
            .Start(Guid.NewGuid(), StageNames.Asr, DateTimeOffset.UtcNow.AddHours(-1))
            .WithRuntimeInfo("dml", "dml")
            .Complete(DateTimeOffset.UtcNow);
        var snapshot = new Dictionary<string, string>
        {
            [$"Provider:{StageNames.Asr}"] = "dml"
        };

        Assert.True(StageArtifactResumeEvaluator.RuntimeMatchesSnapshot(run, StageNames.Asr, snapshot));
    }

    [Fact]
    public void RuntimeMatchesSnapshot_returns_true_when_prior_run_reported_cloud()
    {
        // Cloud engines report "cloud" regardless of local hardware overrides.
        StageRunRecord run = StageRunRecord
            .Start(Guid.NewGuid(), StageNames.Translation, DateTimeOffset.UtcNow.AddHours(-1))
            .WithRuntimeInfo("cloud", "cloud")
            .Complete(DateTimeOffset.UtcNow);
        var snapshot = new Dictionary<string, string>
        {
            [$"Provider:{StageNames.Translation}"] = "dml"
        };

        Assert.True(StageArtifactResumeEvaluator.RuntimeMatchesSnapshot(run, StageNames.Translation, snapshot));
    }

    [Fact]
    public void RuntimeMatchesSnapshot_returns_false_when_provider_expected_but_run_recorded_none()
    {
        StageRunRecord run = StageRunRecord
            .Start(Guid.NewGuid(), StageNames.Asr, DateTimeOffset.UtcNow.AddHours(-1))
            .Complete(DateTimeOffset.UtcNow);
        var snapshot = new Dictionary<string, string>
        {
            [$"Provider:{StageNames.Asr}"] = "dml"
        };

        Assert.False(StageArtifactResumeEvaluator.RuntimeMatchesSnapshot(run, StageNames.Asr, snapshot));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_tts_when_take_text_hash_mismatches_current_text()
    {
        // Take was synthesized for "hola"; the current translated text differs, so the cached
        // take must not be resumed even though the artifact still exists.
        TtsScenario scenario = CreateTtsScenario(
            translatedText: "adios",
            takeTextHash: TtsTextHash.Compute(0, "hola"));

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            scenario.State,
            scenario.ArtifactStore,
            StageNames.Tts,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: scenario.ArtifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_tts_when_voice_override_differs_from_take_voice()
    {
        TtsScenario scenario = CreateTtsScenario(
            takeVoiceId: "af_heart",
            assignmentVoiceVariant: "af_heart");

        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Voice:Speaker 1"] = "am_michael"
        };

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            scenario.State,
            scenario.ArtifactStore,
            StageNames.Tts,
            snapshot,
            projectRootPath: scenario.ArtifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_tts_when_take_voice_matches_assignment_and_no_override()
    {
        TtsScenario scenario = CreateTtsScenario(
            takeVoiceId: "af_heart",
            assignmentVoiceVariant: "af_heart");

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            scenario.State,
            scenario.ArtifactStore,
            StageNames.Tts,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: scenario.ArtifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_tts_when_clone_intent_but_take_is_stock()
    {
        // Clone is now requested for the speaker but the cached take is plain stock with no
        // persisted fallback justification: rerun must actually clone.
        TtsScenario scenario = CreateTtsScenario(
            takeVoiceId: "af_heart",
            assignmentVoiceVariant: "af_heart");

        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"VoiceClone:{scenario.SpeakerId:D}"] = "True"
        };

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            scenario.State,
            scenario.ArtifactStore,
            StageNames.Tts,
            snapshot,
            projectRootPath: scenario.ArtifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_tts_when_clone_intent_and_persisted_fallback()
    {
        // Insufficient-speech fallback persisted as IsFallback with no clip: rerunning under
        // the same clone intent would reach the same fallback, so the take may resume.
        TtsScenario scenario = CreateTtsScenario(
            takeVoiceId: "af_heart",
            assignmentVoiceVariant: "af_heart",
            assignmentIsFallback: true);

        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"VoiceClone:{scenario.SpeakerId:D}"] = "True"
        };

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            scenario.State,
            scenario.ArtifactStore,
            StageNames.Tts,
            snapshot,
            projectRootPath: scenario.ArtifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_tts_when_cloned_take_but_clone_now_disabled()
    {
        TtsScenario scenario = CreateTtsScenario(
            kind: TtsTakeKind.VoiceCloned,
            takeClipArtifactId: Guid.NewGuid(),
            assignmentClipArtifactId: null,
            assignmentIsFallback: false,
            assignmentVoiceModelId: "chatterbox");

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            scenario.State,
            scenario.ArtifactStore,
            StageNames.Tts,
            snapshot: new Dictionary<string, string>(),
            projectRootPath: scenario.ArtifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_tts_when_cloned_take_reference_clip_changed()
    {
        Guid priorClip = Guid.NewGuid();
        Guid currentClip = Guid.NewGuid();
        TtsScenario scenario = CreateTtsScenario(
            kind: TtsTakeKind.VoiceCloned,
            takeClipArtifactId: priorClip,
            assignmentClipArtifactId: currentClip,
            assignmentVoiceModelId: "chatterbox");

        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"VoiceClone:{scenario.SpeakerId:D}"] = "True"
        };

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            scenario.State,
            scenario.ArtifactStore,
            StageNames.Tts,
            snapshot,
            projectRootPath: scenario.ArtifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_true_for_tts_when_cloned_take_clip_still_matches()
    {
        Guid clipId = Guid.NewGuid();
        TtsScenario scenario = CreateTtsScenario(
            kind: TtsTakeKind.VoiceCloned,
            takeClipArtifactId: clipId,
            assignmentClipArtifactId: clipId,
            assignmentVoiceModelId: "chatterbox");

        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"VoiceClone:{scenario.SpeakerId:D}"] = "True"
        };

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            scenario.State,
            scenario.ArtifactStore,
            StageNames.Tts,
            snapshot,
            projectRootPath: scenario.ArtifactStore.GetPath(".")));
    }

    private sealed record TtsScenario(
        TranscriptProjectState State,
        FakeArtifactStore ArtifactStore,
        Guid SpeakerId);

    private static TtsScenario CreateTtsScenario(
        string translatedText = "hola",
        string? takeTextHash = null,
        TtsTakeKind kind = TtsTakeKind.Stock,
        string? takeVoiceId = null,
        Guid? takeClipArtifactId = null,
        string? assignmentVoiceVariant = null,
        string assignmentVoiceModelId = "kokoro",
        bool assignmentIsFallback = false,
        Guid? assignmentClipArtifactId = null)
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StageRunRecord run = StageRunRecord
            .Start(projectId, StageNames.Tts, now.AddHours(-1))
            .Complete(now);

        // Use the record ctor so the speaker id matches TranscriptSegment.SpeakerId;
        // ProjectSpeaker.Create would mint a fresh id that never links to the segment.
        var speaker = new ProjectSpeaker(speakerId, projectId, "Speaker 1", now);

        var transcriptRevision = TranscriptRevision.Create(projectId, run.Id, 1, now);
        TranscriptSegment transcriptSegment = TranscriptSegment.Create(
            transcriptRevision.Id,
            0,
            0.0,
            1.5,
            "hello",
            speakerId,
            "en");

        TranslationRevision translationRevision = TranslationRevision.Create(
            projectId,
            run.Id,
            transcriptRevision.Id,
            "es",
            revisionNumber: 1,
            now);
        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            translationRevision.Id,
            0,
            0.0,
            1.5,
            translatedText);

        VoiceAssignment assignment = VoiceAssignment.Create(
            projectId,
            speakerId,
            assignmentVoiceModelId,
            voiceVariant: assignmentVoiceVariant,
            isFallback: assignmentIsFallback,
            referenceClipArtifactId: assignmentClipArtifactId);

        const string takePath = "artifacts/tts/run/segment-0.wav";
        artifactStore.Seed(takePath);
        Guid artifactId = Guid.NewGuid();
        ProjectArtifact takeArtifact = new(
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

        TtsTake take = (
            kind == TtsTakeKind.VoiceCloned
                ? TtsTake.CreateVoiceCloned(
                    projectId,
                    assignment.Id,
                    takeClipArtifactId ?? Guid.NewGuid(),
                    translatedSegment.Id,
                    segmentIndex: 0,
                    translatedTextHash: takeTextHash ?? TtsTextHash.Compute(0, translatedText))
                : TtsTake.CreateStock(
                    projectId,
                    assignment.Id,
                    translatedSegment.Id,
                    segmentIndex: 0,
                    translatedTextHash: takeTextHash ?? TtsTextHash.Compute(0, translatedText)))
            .Complete(
                artifactId,
                run.Id,
                durationSamples: 24000,
                sampleRate: 24000,
                provider: "test",
                modelId: null,
                voiceId: takeVoiceId,
                durationOverrunRatio: null);

        TranscriptProjectState state = CreateState(
            projectId,
            [run],
            [takeArtifact],
            currentTranslationRevision: translationRevision,
            translatedSegments: [translatedSegment],
            ttsTakes: [take],
            currentTranscriptRevision: transcriptRevision,
            transcriptSegments: [transcriptSegment],
            speakers: [speaker],
            voiceAssignments: [assignment]);

        return new TtsScenario(state, artifactStore, speakerId);
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
        string transcriptLanguage = "en",
        IReadOnlyList<ProjectSpeaker>? speakers = null,
        IReadOnlyList<VoiceAssignment>? voiceAssignments = null,
        SourceMediaStatus sourceStatus = SourceMediaStatus.Available,
        SourceMediaReference? sourceReference = null)
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
            SourceReference: sourceReference,
            sourceStatus,
            SourceStatusMessage: null,
            Artifacts: artifacts ?? [],
            TranscriptLanguage: transcriptLanguage);

        return new TranscriptProjectState(
            openResult,
            CurrentTranscriptRevision: currentTranscriptRevision,
            TranscriptSegments: transcriptSegments ?? [],
            Speakers: speakers ?? [],
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
            VoiceAssignments: voiceAssignments ?? [],
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

    [Fact]
    public void CanResumeStage_returns_true_for_asr_when_canonical_source_language_matches()
    {
        (TranscriptProjectState state, FakeArtifactStore artifactStore) =
            CreateAsrResumableState(persistedLanguage: "en");

        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Asr,
            snapshot: new Dictionary<string, string> { ["SourceLanguage"] = "en" },
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_returns_false_for_asr_when_canonical_source_language_differs()
    {
        (TranscriptProjectState state, FakeArtifactStore artifactStore) =
            CreateAsrResumableState(persistedLanguage: "en");

        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Asr,
            snapshot: new Dictionary<string, string> { ["SourceLanguage"] = "fr" },
            projectRootPath: artifactStore.GetPath(".")));
    }

    [Fact]
    public void CanResumeStage_asr_honors_legacy_source_language_code_key()
    {
        (TranscriptProjectState state, FakeArtifactStore artifactStore) =
            CreateAsrResumableState(persistedLanguage: "en");

        // Snapshots captured before the key alignment wrote "SourceLanguageCode".
        Assert.True(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Asr,
            snapshot: new Dictionary<string, string> { ["SourceLanguageCode"] = "en" },
            projectRootPath: artifactStore.GetPath(".")));
        Assert.False(StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            StageNames.Asr,
            snapshot: new Dictionary<string, string> { ["SourceLanguageCode"] = "fr" },
            projectRootPath: artifactStore.GetPath(".")));
    }

    private static (TranscriptProjectState State, FakeArtifactStore Store) CreateAsrResumableState(
        string persistedLanguage)
    {
        var artifactStore = new FakeArtifactStore();
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        StageRunRecord asrRun = StageRunRecord
            .Start(projectId, StageNames.Asr, now.AddHours(-1))
            .Complete(now);
        var revision = TranscriptRevision.Create(projectId, asrRun.Id, 1, now);
        var segment = TranscriptSegment.Create(revision.Id, 0, 0.0, 1.0, "hello");

        const string rawTranscriptPath = "artifacts/transcript/transcript-revision-raw.json";
        artifactStore.Seed(rawTranscriptPath);
        ProjectArtifact rawArtifact = new(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.TranscriptRevision,
            rawTranscriptPath,
            "hash",
            64,
            null,
            null,
            null,
            now,
            StageRunId: asrRun.Id,
            Provenance: "generated-asr-raw");

        TranscriptProjectState state = CreateState(
            projectId,
            [asrRun],
            [rawArtifact],
            currentTranscriptRevision: revision,
            transcriptSegments: [segment],
            transcriptLanguage: persistedLanguage);

        return (state, artifactStore);
    }
}
