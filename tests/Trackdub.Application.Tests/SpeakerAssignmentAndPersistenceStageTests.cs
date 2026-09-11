using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Application.Transcripts.Stages;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Contracts.Licensing;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class SpeakerAssignmentAndPersistenceStageTests
{
    [Fact]
    public async Task ExecuteAsync_seeds_asr_segment_stage_run_map_on_initial_transcript_completion()
    {
        var artifactStore = new FakeArtifactStore();
        var transcriptRepository = new FakeTranscriptRepository();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var fingerprintService = new FakeFileFingerprintService();
        var artifactWriter = new TranscriptArtifactWriter(artifactStore, fingerprintService, mediaAssetRepository);
        var speakerAssignmentService = new SpeakerAssignmentService(
            new FakeSpeakerRepository(),
            transcriptRepository,
            new SegmentEditingService(transcriptRepository, new FakeTtsTakeRepository(), artifactWriter),
            artifactStore,
            new FakeProjectStageRunStore(),
            new FakeDiarizationEngine(),
            new SpeakerReferenceClipService(
                artifactStore,
                new FakeAudioClipExtractor(),
                fingerprintService,
                mediaAssetRepository,
                new FakeVoiceAssignmentRepository(),
                new FakeTtsTakeRepository(),
                new FakeReferenceClipAnalyzer(),
                new FakeReferenceClipTrimmer()),
            artifactWriter,
            new DiarizationStageHandler(
                new FakeDiarizationEngine(),
                new WritingModelDownloader(),
                modelCacheRoot: Path.Join(Path.GetTempPath(), "trackdub-tests", Guid.NewGuid().ToString("N")),
                expectedSha256: SortFormerTestFixtures.ExpectedSha256));
        var stageRunStore = new FakeProjectStageRunStore();
        var stage = new SpeakerAssignmentAndPersistenceStage(
            speakerAssignmentService,
            transcriptRepository,
            artifactWriter,
            artifactStore,
            stageRunStore);

        TranscriptGenerationContext context = CreateContext();
        Guid asrStageRunId = context.AsrResult!.StageRun.Id;
        await artifactStore.WriteJsonAsync(
            ProjectArtifactPaths.ManifestRelativePath,
            ProjectManifest.FromProject(context.Project),
            TestContext.Current.CancellationToken);

        await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);

        ProjectManifest? manifest = await artifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            TestContext.Current.CancellationToken);
        Assert.NotNull(manifest?.UiSettings?.SegmentStageRuns?.Asr);
        Assert.Equal(asrStageRunId, manifest.UiSettings.SegmentStageRuns.Asr[0]);
        Assert.Equal(asrStageRunId, manifest.UiSettings.SegmentStageRuns.Asr[1]);
    }

    [Fact]
    public async Task ExecuteAsync_skips_blank_asr_regions_instead_of_failing()
    {
        var artifactStore = new FakeArtifactStore();
        var transcriptRepository = new FakeTranscriptRepository();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var fingerprintService = new FakeFileFingerprintService();
        var artifactWriter = new TranscriptArtifactWriter(artifactStore, fingerprintService, mediaAssetRepository);
        var speakerAssignmentService = new SpeakerAssignmentService(
            new FakeSpeakerRepository(),
            transcriptRepository,
            new SegmentEditingService(transcriptRepository, new FakeTtsTakeRepository(), artifactWriter),
            artifactStore,
            new FakeProjectStageRunStore(),
            new FakeDiarizationEngine(),
            new SpeakerReferenceClipService(
                artifactStore,
                new FakeAudioClipExtractor(),
                fingerprintService,
                mediaAssetRepository,
                new FakeVoiceAssignmentRepository(),
                new FakeTtsTakeRepository(),
                new FakeReferenceClipAnalyzer(),
                new FakeReferenceClipTrimmer()),
            artifactWriter,
            new DiarizationStageHandler(
                new FakeDiarizationEngine(),
                new WritingModelDownloader(),
                modelCacheRoot: Path.Join(Path.GetTempPath(), "trackdub-tests", Guid.NewGuid().ToString("N")),
                expectedSha256: SortFormerTestFixtures.ExpectedSha256));
        var stage = new SpeakerAssignmentAndPersistenceStage(
            speakerAssignmentService,
            transcriptRepository,
            artifactWriter,
            artifactStore,
            new FakeProjectStageRunStore());

        TranscriptGenerationContext context = CreateContext(
        [
            new RecognizedTranscriptSegment(0, 0.0d, 2.0d, "Hello"),
            new RecognizedTranscriptSegment(1, 2.0d, 3.0d, "   "),
            new RecognizedTranscriptSegment(2, 3.0d, 4.0d, "World"),
        ]);
        await artifactStore.WriteJsonAsync(
            ProjectArtifactPaths.ManifestRelativePath,
            ProjectManifest.FromProject(context.Project),
            TestContext.Current.CancellationToken);

        await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);

        TranscriptRevision revision = Assert.Single(transcriptRepository.Revisions);
        IReadOnlyList<TranscriptSegment> saved = await transcriptRepository.GetSegmentsAsync(
            revision.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, saved.Count);
        Assert.Equal("Hello", saved[0].Text);
        Assert.Equal("World", saved[1].Text);
        Assert.Equal(0, saved[0].SegmentIndex);
        Assert.Equal(2, saved[1].SegmentIndex);
    }

    private static TranscriptGenerationContext CreateContext(
        IReadOnlyList<RecognizedTranscriptSegment>? asrSegments = null)
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "Demo", now, now);
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            10.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var audioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.NormalizedAudio,
            "artifacts/audio.wav",
            "audio-hash",
            100,
            10.0d,
            16000,
            1,
            now);
        var asrStageRun = StageRunRecord.Start(projectId, StageNames.Asr, now);
        var asrResult = new AsrStageResult(
            asrStageRun,
            asrSegments ??
            [
                new RecognizedTranscriptSegment(0, 0.0d, 2.0d, "Hello"),
                new RecognizedTranscriptSegment(1, 2.0d, 4.0d, "World"),
            ]);
        TranscriptAudioRoutingPlan routingPlan = TranscriptAudioRoutingPlan.Raw(
            audioArtifact,
            SpeechAudioSourceKind.FullMix);
        TranscriptRegionPlan regionPlan = TranscriptWorkflowUtilities.BuildTranscriptRegionPlan(
            [new SpeechRegion(0, 0.0d, 10.0d)],
            diarizationResult: null,
            durationSeconds: 10.0d);

        return new TranscriptGenerationContext(
            project,
            mediaAsset,
            audioArtifact,
            routingPlan,
            enableSpeakerDiarization: false,
            sourceLanguage: "en")
        {
            AsrResult = asrResult,
            RegionPlan = regionPlan,
        };
    }

    private sealed class WritingModelDownloader : IModelDownloaderContract
    {
        public async Task<bool> DownloadAsync(
            string modelId,
            string fileName,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? revision = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, SortFormerTestFixtures.ModelBytes, cancellationToken);
            return true;
        }

        public Task<bool> DownloadUriAsync(
            Uri sourceUri,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
