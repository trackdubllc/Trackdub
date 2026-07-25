using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class OverlapRescueStageHandlerTests
{
    [Fact]
    public async Task HandleAsync_WithNoOverlapRegions_SkipsStageRun()
    {
        string directory = CreateTempDirectory();
        try
        {
            var artifactStore = new FakeArtifactStore(directory);
            (MediaAsset mediaAsset, ProjectArtifact sourceArtifact) = SeedSource(artifactStore, directory);
            var engine = new FakeOverlapRescueEngine();
            var handler = CreateHandler(engine, artifactStore, new FakeMediaAssetRepository(), new FakeProjectStageRunStore());

            OverlapRescueStageResult result = await handler.HandleAsync(
                new OverlapRescueStageRequest(
                    mediaAsset.ProjectId,
                    mediaAsset,
                    sourceArtifact,
                    Regions: [],
                    ExistingArtifacts: []),
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(StageRunStatus.Skipped, result.StageRun.Status);
            Assert.Empty(result.Regions);
            Assert.Equal(0, engine.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HandleAsync_WithOverlapRegions_CommitsSourceCandidateArtifacts()
    {
        string directory = CreateTempDirectory();
        try
        {
            var artifactStore = new FakeArtifactStore(directory);
            (MediaAsset mediaAsset, ProjectArtifact sourceArtifact) = SeedSource(artifactStore, directory);
            var mediaRepository = new FakeMediaAssetRepository();
            var stageRunStore = new FakeProjectStageRunStore();
            var engine = new FakeOverlapRescueEngine();
            var handler = CreateHandler(engine, artifactStore, mediaRepository, stageRunStore);
            var regions = new List<OverlapRegion>
            {
                new(1.0d, 3.0d)
            };

            OverlapRescueStageResult result = await handler.HandleAsync(
                new OverlapRescueStageRequest(
                    mediaAsset.ProjectId,
                    mediaAsset,
                    sourceArtifact,
                    regions,
                    ExistingArtifacts: []),
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
            Assert.Single(result.Regions);
            Assert.Equal(1, engine.CallCount);
            Guid stageRunId = result.StageRun.Id;
            Assert.Equal(
                ProjectArtifactPaths.GetOverlapSourceCandidateRelativePath(stageRunId, 0, 0),
                result.Regions[0].SourceCandidate0.RelativePath);
            Assert.Equal(
                ProjectArtifactPaths.GetOverlapSourceCandidateRelativePath(stageRunId, 0, 1),
                result.Regions[0].SourceCandidate1.RelativePath);
            Assert.True(artifactStore.Exists(result.Regions[0].SourceCandidate0.RelativePath));
            Assert.True(artifactStore.Exists(result.Regions[0].SourceCandidate1.RelativePath));
            Assert.Contains("source_candidate=0", result.Regions[0].SourceCandidate0.Provenance, StringComparison.Ordinal);
            Assert.Contains("overlap_detection=diarization", result.Regions[0].SourceCandidate0.Provenance, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HandleAsync_WhenEngineFails_MarksStageRunFailed()
    {
        string directory = CreateTempDirectory();
        try
        {
            var artifactStore = new FakeArtifactStore(directory);
            (MediaAsset mediaAsset, ProjectArtifact sourceArtifact) = SeedSource(artifactStore, directory);
            var stageRunStore = new FakeProjectStageRunStore();
            var engine = new FakeOverlapRescueEngine { ThrowOnRescue = true };
            var handler = CreateHandler(engine, artifactStore, new FakeMediaAssetRepository(), stageRunStore);

            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
                new OverlapRescueStageRequest(
                    mediaAsset.ProjectId,
                    mediaAsset,
                    sourceArtifact,
                    [new OverlapRegion(0.5d, 2.0d)],
                    ExistingArtifacts: []),
                progress: null,
                TestContext.Current.CancellationToken));

            StageRunRecord? failedRun = stageRunStore.All
                .SingleOrDefault(run => run.StageName == StageNames.OverlapRescue);
            Assert.NotNull(failedRun);
            Assert.Equal(StageRunStatus.Failed, failedRun.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HandleAsync_WithPermutationWarning_MarksStagePartiallyComplete()
    {
        string directory = CreateTempDirectory();
        try
        {
            var artifactStore = new FakeArtifactStore(directory);
            (MediaAsset mediaAsset, ProjectArtifact sourceArtifact) = SeedSource(artifactStore, directory);
            var engine = new FakeOverlapRescueEngine { PermutationWarning = true };
            var handler = CreateHandler(
                engine,
                artifactStore,
                new FakeMediaAssetRepository(),
                new FakeProjectStageRunStore());

            OverlapRescueStageResult result = await handler.HandleAsync(
                new OverlapRescueStageRequest(
                    mediaAsset.ProjectId,
                    mediaAsset,
                    sourceArtifact,
                    [new OverlapRegion(4.0d, 8.0d)],
                    ExistingArtifacts: []),
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(StageRunStatus.PartiallyCompleted, result.StageRun.Status);
            Assert.True(result.Regions[0].PermutationWarning);
            Assert.Contains("permutation_warning=true", result.Regions[0].SourceCandidate0.Provenance, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OverlapRegionDetector_MergesAdjacentOverlapTurns()
    {
        var detector = new OverlapRegionDetector();
        SpeakerTurn[] turns =
        [
            SpeakerTurn.Create(Guid.NewGuid(), Guid.NewGuid(), 1.0d, 2.0d, hasOverlap: true),
            SpeakerTurn.Create(Guid.NewGuid(), Guid.NewGuid(), 2.2d, 3.0d, hasOverlap: true)
        ];

        IReadOnlyList<OverlapRegion> regions = detector.DetectFromSpeakerTurns(turns, mediaDurationSeconds: 10d);

        Assert.Single(regions);
        Assert.True(regions[0].StartSeconds < 1.0d);
        Assert.True(regions[0].EndSeconds > 3.0d);
    }

    private static OverlapRescueStageHandler CreateHandler(
        IOverlapRescueEngine engine,
        FakeArtifactStore artifactStore,
        FakeMediaAssetRepository mediaRepository,
        FakeProjectStageRunStore stageRunStore) =>
        new(
            engine,
            new RecordingAudioClipExtractor(),
            artifactStore,
            new FakeFileFingerprintService(),
            mediaRepository,
            stageRunStore);

    private static (MediaAsset MediaAsset, ProjectArtifact SourceArtifact) SeedSource(
        FakeArtifactStore artifactStore,
        string directory)
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        string sourceRelativePath = ProjectArtifactPaths.NormalizedAudioRelativePath;
        string sourcePath = Path.Combine(directory, "source.wav");
        File.WriteAllBytes(sourcePath, FakeWavHelper.MinimalPcm16(durationSeconds: 1d, sampleRate: 16000, channelCount: 1));
        artifactStore.SeedPath(sourceRelativePath, sourcePath, File.ReadAllBytes(sourcePath));

        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            sourcePath,
            "source.wav",
            "source-hash",
            new FileInfo(sourcePath).Length,
            DateTimeOffset.UtcNow,
            "wav",
            10d,
            HasAudio: true,
            HasVideo: false,
            DateTimeOffset.UtcNow);
        var artifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.NormalizedAudio,
            sourceRelativePath,
            "source-hash",
            new FileInfo(sourcePath).Length,
            10d,
            16000,
            1,
            DateTimeOffset.UtcNow);
        return (mediaAsset, artifact);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"overlap-rescue-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingAudioClipExtractor : IAudioClipExtractor
    {
        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            double startSeconds,
            double endSeconds,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (File.Exists(sourceWavePath))
            {
                File.Copy(sourceWavePath, destinationPath, overwrite: true);
            }
            else
            {
                File.WriteAllBytes(destinationPath, FakeWavHelper.MinimalPcm16());
            }

            return Task.FromResult(new AudioClipExtractionResult(
                destinationPath,
                Math.Max(0d, endSeconds - startSeconds),
                16000,
                1));
        }

        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            IReadOnlyList<AudioClipRange> ranges,
            string destinationPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
