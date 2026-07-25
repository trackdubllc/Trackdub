using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.LipSync;
using Trackdub.Domain.Media;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class LipSyncStageHandlerTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lipsync-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static MediaAsset MakeMediaAsset(Guid? projectId = null) => new(
        Id: Guid.NewGuid(),
        ProjectId: projectId ?? Guid.NewGuid(),
        SourceFilePath: "/tmp/test.mp4",
        SourceFileName: "test.mp4",
        FingerprintSha256: "abc",
        SourceSizeBytes: 0L,
        SourceLastWriteTimeUtc: DateTimeOffset.UtcNow,
        FormatName: "mp4",
        DurationSeconds: 10.0,
        HasAudio: true,
        HasVideo: true,
        CreatedAtUtc: DateTimeOffset.UtcNow);

    private static LipSyncStageHandler MakeHandler(
        FakeArtifactStore artifactStore,
        FakeProjectStageRunStore stageRunStore,
        FakeForcedAligner? aligner = null,
        FakePhonemeStretchService? stretchService = null,
        FakeAudioClipExtractor? clipExtractor = null,
        IMediaAssetRepository? mediaAssetRepository = null)
    {
        return new LipSyncStageHandler(
            forcedAligner: aligner ?? new FakeForcedAligner(),
            phonemeTimingPlanner: new FakePhonemeTimingPlanner(),
            phonemeStretchService: stretchService ?? new FakePhonemeStretchService(),
            artifactStore: artifactStore,
            stageRunStore: stageRunStore,
            audioClipExtractor: clipExtractor,
            mediaAssetRepository: mediaAssetRepository);
    }

    // ---------------------------------------------------------------------------
    // Success path
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WithEnabledStageAndSuccessfulAlignment_ReturnsAlignedSegments()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Success,
                OverallConfidence = 0.95,
                PhonesToReturn = [new PhonemeTiming("AH", "arpabet", TimeSpan.Zero, TimeSpan.FromMilliseconds(100), 0.9)]
            };
            var stretchService = new FakePhonemeStretchService
            {
                AlignedDurationToReturn = TimeSpan.FromSeconds(2.0)
            };

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();
            var translatedSegmentId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: translatedSegmentId) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId,
                ProjectId: projectId,
                MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake,
                RelativePath: relPath,
                Sha256: "abc",
                SizeBytes: 0L,
                DurationSeconds: null,
                SampleRate: null,
                ChannelCount: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner, stretchService);

            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact]),
                TestContext.Current.CancellationToken);

            // Stretch is short-circuited to Partial because source phonemes are not yet
            // available (no forced alignment on the source audio). The segment reports
            // Partial with a skip reason; the stretch service is never called.
            Assert.Equal(StageRunStatus.PartiallyCompleted, result.StageRun.Status);
            Assert.Single(result.Segments);
            Assert.Equal(LipSyncSegmentStatus.Partial, result.Segments[0].Status);
            Assert.Equal(translatedSegmentId, result.Segments[0].SegmentId);
            Assert.Null(result.Segments[0].AlignedTtsDuration);
            Assert.NotNull(result.Segments[0].SkipReason);
            Assert.Equal(1, aligner.CallCount);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Skip: stage disabled
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenStageDisabled_SkipsWithoutCallingAligner()
    {
        string directory = CreateTempDirectory();
        try
        {
            var mediaAsset = MakeMediaAsset();
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();
            var aligner = new FakeForcedAligner();
            var handler = MakeHandler(artifactStore, stageRunStore, aligner);

            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: mediaAsset.ProjectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [TtsTake.CreateStock(mediaAsset.ProjectId, Guid.NewGuid())],
                    ExistingArtifacts: [],
                    IsEnabled: false),
                TestContext.Current.CancellationToken);

            Assert.Equal(StageRunStatus.Skipped, result.StageRun.Status);
            Assert.Empty(result.Segments);
            Assert.Equal(0, aligner.CallCount);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Skip: take has no artifact
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenTakeHasNoArtifact_SegmentSkippedNoPhonemes()
    {
        string directory = CreateTempDirectory();
        try
        {
            var mediaAsset = MakeMediaAsset();
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();
            var aligner = new FakeForcedAligner();
            var handler = MakeHandler(artifactStore, stageRunStore, aligner);

            // Take has ArtifactId = null → ProcessTakeAsync skips before calling aligner
            var take = TtsTake.CreateStock(
                projectId: mediaAsset.ProjectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: Guid.NewGuid());

            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: mediaAsset.ProjectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: []),
                TestContext.Current.CancellationToken);

            Assert.Equal(LipSyncSegmentStatus.SkippedNoPhonemes, result.Segments[0].Status);
            Assert.Equal(0, aligner.CallCount);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Skip: aligner returns Skipped
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenAlignerReturnsSkipped_SegmentIsSkippedLowConfidence()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Skipped,
                SkipReasonToReturn = "Low confidence"
            };

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: Guid.NewGuid()) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner);

            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact]),
                TestContext.Current.CancellationToken);

            Assert.Equal(LipSyncSegmentStatus.SkippedLowConfidence, result.Segments[0].Status);
            Assert.Equal("Low confidence", result.Segments[0].SkipReason);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Skip: stretch service returns null (unsafe ratio)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenStretchServiceReturnsNull_SegmentSkippedUnsafeStretchRatio()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Success,
                OverallConfidence = 0.95,
                PhonesToReturn = [new PhonemeTiming("AH", "arpabet", TimeSpan.Zero, TimeSpan.FromMilliseconds(100), 0.9)]
            };
            var stretchService = new FakePhonemeStretchService { ReturnNull = true };

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: Guid.NewGuid()) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner, stretchService);

            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact]),
                TestContext.Current.CancellationToken);

            // With source phonemes unavailable, the stretch path is never reached;
            // the segment returns Partial with a skip reason instead.
            Assert.Equal(LipSyncSegmentStatus.Partial, result.Segments[0].Status);
            Assert.NotNull(result.Segments[0].SkipReason);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Failure path: aligner throws
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenAlignerThrows_StageRunFailsAndRethrows()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            var aligner = new FakeForcedAligner { ThrowOnAlign = true };

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: Guid.NewGuid()) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    new LipSyncStageRequest(
                        ProjectId: projectId,
                        MediaAsset: mediaAsset,
                        TtsTakes: [take],
                        ExistingArtifacts: [artifact]),
                    TestContext.Current.CancellationToken));

            Assert.Equal(StageRunStatus.Failed, stageRunStore.All.Last().Status);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Cancellation path
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenCanceled_StageRunCanceledAndRethrows()
    {
        string directory = CreateTempDirectory();
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var mediaAsset = MakeMediaAsset();
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();
            var handler = MakeHandler(artifactStore, stageRunStore);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                handler.HandleAsync(
                    new LipSyncStageRequest(
                        ProjectId: mediaAsset.ProjectId,
                        MediaAsset: mediaAsset,
                        TtsTakes: [TtsTake.CreateStock(mediaAsset.ProjectId, Guid.NewGuid())],
                        ExistingArtifacts: [],
                        IsEnabled: true),
                    cts.Token));

            Assert.Equal(StageRunStatus.Canceled, stageRunStore.All.Last().Status);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Artifact preservation: original TTS artifact not modified on skip
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenAlignmentSkipped_OriginalTtsArtifactIsPreserved()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Skipped,
                SkipReasonToReturn = "low-confidence"
            };

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            byte[] originalContent = "RIFF"u8.ToArray(); // "RIFF"
            // Seed with specific content — Seed creates the file on disk with those bytes.
            artifactStore.Seed(relPath, originalContent);
            var absPath = artifactStore.GetPath(relPath);

            var artifactId = Guid.NewGuid();
            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: Guid.NewGuid()) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner);

            await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact]),
                TestContext.Current.CancellationToken);

            // Original file must remain byte-for-byte identical.
            var actualContent = await File.ReadAllBytesAsync(absPath, TestContext.Current.CancellationToken);
            Assert.Equal(originalContent, actualContent);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Source alignment + stretch path: success (full stretch with source timing)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WithSourceTimingAndClipExtractor_StretchPathProducesAlignedSegment()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            var phonemes = new List<PhonemeTiming>
            {
                new("AH", "arpabet", TimeSpan.Zero, TimeSpan.FromMilliseconds(100), 0.9),
                new("T",  "arpabet", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), 0.85)
            };
            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Success,
                OverallConfidence = 0.95,
                PhonesToReturn = phonemes
            };
            var stretchService = new FakePhonemeStretchService
            {
                AlignedDurationToReturn = TimeSpan.FromSeconds(1.8)
            };
            var clipExtractor = new FakeAudioClipExtractor();

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();
            var translatedSegmentId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: translatedSegmentId) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            // Build source timing map: segment → 1.0–4.0 s in source audio.
            var sourceTiming = new Dictionary<Guid, SegmentSourceTiming>
            {
                [translatedSegmentId] = new(1.0, 4.0)
            };
            string fakeSourcePath = Path.Combine(directory, "source.wav");
            File.WriteAllBytes(fakeSourcePath, []);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner, stretchService, clipExtractor);

            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact],
                    SegmentSourceTimingMap: sourceTiming,
                    SourceSegmentTranscriptMap: new Dictionary<Guid, string> { [translatedSegmentId] = "hello world" },
                    SourceAudioPath: fakeSourcePath),
                TestContext.Current.CancellationToken);

            // Clip extractor should have been called for the source segment.
            Assert.Equal(1, clipExtractor.CallCount);
            Assert.Equal(fakeSourcePath, clipExtractor.LastSourcePath);
            Assert.Equal(1.0, clipExtractor.LastStartSeconds);
            Assert.Equal(4.0, clipExtractor.LastEndSeconds);

            // Aligner called twice: once for TTS, once for source.
            Assert.Equal(2, aligner.CallCount);

            // Stretch service called once.
            Assert.Equal(1, stretchService.CallCount);

            // Segment should be Aligned with a filled AlignedTtsDuration.
            Assert.Single(result.Segments);
            var seg = result.Segments[0];
            Assert.Equal(LipSyncSegmentStatus.Aligned, seg.Status);
            Assert.Equal(TimeSpan.FromSeconds(1.8), seg.AlignedTtsDuration);
            Assert.Equal(translatedSegmentId, seg.SegmentId);
            Assert.Null(seg.FailureReason);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Source alignment + stretch path: clip extraction fails → Partial fallback
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenClipExtractionFails_SegmentIsPartialWithSkipReason()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            var phonemes = new List<PhonemeTiming>
            {
                new("AH", "arpabet", TimeSpan.Zero, TimeSpan.FromMilliseconds(100), 0.9)
            };
            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Success,
                OverallConfidence = 0.95,
                PhonesToReturn = phonemes
            };
            var clipExtractor = new FakeAudioClipExtractor { ThrowOnExtract = true };

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();
            var translatedSegmentId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: translatedSegmentId) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var sourceTiming = new Dictionary<Guid, SegmentSourceTiming>
            {
                [translatedSegmentId] = new(0.0, 2.5)
            };
            string fakeSourcePath = Path.Combine(directory, "source.wav");
            File.WriteAllBytes(fakeSourcePath, []);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner, clipExtractor: clipExtractor);

            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact],
                    SegmentSourceTimingMap: sourceTiming,
                    SourceSegmentTranscriptMap: new Dictionary<Guid, string> { [translatedSegmentId] = "hello world" },
                    SourceAudioPath: fakeSourcePath),
                TestContext.Current.CancellationToken);

            var seg = result.Segments[0];
            Assert.Equal(LipSyncSegmentStatus.Partial, seg.Status);
            Assert.NotNull(seg.SkipReason);
            Assert.Contains("extraction failed", seg.SkipReason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Source alignment + stretch path: TTS aligner returns no phonemes → SkippedNoPhonemes
    // (source alignment is never reached, proving the pre-source guard)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenTtsAlignmentReturnsNoPhonemes_SegmentSkippedBeforeSourceAlignment()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            // Aligner returns success with empty phoneme list.
            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Success,
                OverallConfidence = 0.92,
                PhonesToReturn = []
            };
            var clipExtractor = new FakeAudioClipExtractor();

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();
            var translatedSegmentId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: translatedSegmentId) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var sourceTiming = new Dictionary<Guid, SegmentSourceTiming>
            {
                [translatedSegmentId] = new(0.5, 3.5)
            };
            string fakeSourcePath = Path.Combine(directory, "source.wav");
            File.WriteAllBytes(fakeSourcePath, []);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner, clipExtractor: clipExtractor);

            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact],
                    SegmentSourceTimingMap: sourceTiming,
                    SourceSegmentTranscriptMap: new Dictionary<Guid, string> { [translatedSegmentId] = "hello world" },
                    SourceAudioPath: fakeSourcePath),
                TestContext.Current.CancellationToken);

            // TTS phonemes are empty → SkippedNoPhonemes before source alignment runs.
            var seg = result.Segments[0];
            Assert.Equal(LipSyncSegmentStatus.SkippedNoPhonemes, seg.Status);

            // Clip extractor must not have been called (source alignment was never reached).
            Assert.Equal(0, clipExtractor.CallCount);

            // Aligner called only once (for TTS take).
            Assert.Equal(1, aligner.CallCount);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Artifact registration: LipSyncTake artifact saved with correct provenance
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenStretchSucceeds_RegistersLipSyncTakeArtifact()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();
            var mediaRepo = new FakeMediaAssetRepository();

            var phonemes = new List<PhonemeTiming>
            {
                new("AH", "arpabet", TimeSpan.Zero, TimeSpan.FromMilliseconds(100), 0.9),
                new("T",  "arpabet", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), 0.85)
            };
            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Success,
                OverallConfidence = 0.95,
                PhonesToReturn = phonemes
            };
            var stretchService = new FakePhonemeStretchService
            {
                AlignedDurationToReturn = TimeSpan.FromSeconds(1.8)
            };
            var clipExtractor = new FakeAudioClipExtractor();

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();
            var translatedSegmentId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: translatedSegmentId) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var sourceTiming = new Dictionary<Guid, SegmentSourceTiming>
            {
                [translatedSegmentId] = new(1.0, 4.0)
            };
            string fakeSourcePath = Path.Combine(directory, "source.wav");
            File.WriteAllBytes(fakeSourcePath, []);

            var handler = MakeHandler(
                artifactStore, stageRunStore, aligner, stretchService, clipExtractor,
                mediaAssetRepository: mediaRepo);

            await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact],
                    SegmentSourceTimingMap: sourceTiming,
                    SourceSegmentTranscriptMap: new Dictionary<Guid, string> { [translatedSegmentId] = "hello world" },
                    SourceAudioPath: fakeSourcePath),
                TestContext.Current.CancellationToken);

            // Exactly one artifact should be registered.
            Assert.Single(mediaRepo.Artifacts);

            var saved = mediaRepo.Artifacts[0];
            Assert.Equal(ArtifactKind.LipSyncTake, saved.Kind);
            Assert.Equal(projectId, saved.ProjectId);
            Assert.Equal(mediaAsset.Id, saved.MediaAssetId);

            // Provenance must encode the TTS take ID so MixPlanBuilder can join on it.
            Assert.Equal($"lipsync:take:{take.Id:N}", saved.Provenance);

            // Duration should reflect what the stretch service reported.
            Assert.Equal(1.8, saved.DurationSeconds);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Artifact registration: no repository provided → no exception, no artifact
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenMediaRepoAbsent_NoArtifactRegisteredAndNoException()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();
            // No mediaAssetRepository → handler should not throw.

            var phonemes = new List<PhonemeTiming>
            {
                new("AH", "arpabet", TimeSpan.Zero, TimeSpan.FromMilliseconds(100), 0.9),
                new("T",  "arpabet", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), 0.85)
            };
            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Success,
                OverallConfidence = 0.95,
                PhonesToReturn = phonemes
            };
            var stretchService = new FakePhonemeStretchService
            {
                AlignedDurationToReturn = TimeSpan.FromSeconds(1.5)
            };
            var clipExtractor = new FakeAudioClipExtractor();

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();
            var translatedSegmentId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: translatedSegmentId) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var sourceTiming = new Dictionary<Guid, SegmentSourceTiming>
            {
                [translatedSegmentId] = new(0.5, 3.0)
            };
            string fakeSourcePath = Path.Combine(directory, "source.wav");
            File.WriteAllBytes(fakeSourcePath, []);

            var handler = MakeHandler(
                artifactStore, stageRunStore, aligner, stretchService, clipExtractor,
                mediaAssetRepository: null);

            // Should not throw even though no repository is provided.
            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact],
                    SegmentSourceTimingMap: sourceTiming,
                    SourceSegmentTranscriptMap: new Dictionary<Guid, string> { [translatedSegmentId] = "hello world" },
                    SourceAudioPath: fakeSourcePath),
                TestContext.Current.CancellationToken);

            // Segment should still be Aligned.
            Assert.Equal(LipSyncSegmentStatus.Aligned, result.Segments[0].Status);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Transcript split: TTS alignment gets translated text, source alignment gets
    // original-language text, and routing options propagate to every aligner call.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_TtsAlignmentUsesTranslatedText_SourceAlignmentUsesSourceText()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            var phonemes = new List<PhonemeTiming>
            {
                new("AH", "arpabet", TimeSpan.Zero, TimeSpan.FromMilliseconds(100), 0.9)
            };
            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Success,
                OverallConfidence = 0.95,
                PhonesToReturn = phonemes
            };
            var clipExtractor = new FakeAudioClipExtractor();

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();
            var translatedSegmentId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: translatedSegmentId) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var sourceTiming = new Dictionary<Guid, SegmentSourceTiming>
            {
                [translatedSegmentId] = new(1.0, 4.0)
            };
            string fakeSourcePath = Path.Combine(directory, "source.wav");
            File.WriteAllBytes(fakeSourcePath, []);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner, clipExtractor: clipExtractor);

            await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact],
                    PreferredModelAlias: "wav2vec2-lv60-espeak-cv-ft-onnx",
                    SegmentTranscriptMap: new Dictionary<Guid, string> { [translatedSegmentId] = "hola mundo" },
                    SegmentSourceTimingMap: sourceTiming,
                    SourceSegmentTranscriptMap: new Dictionary<Guid, string> { [translatedSegmentId] = "hello world" },
                    SourceAudioPath: fakeSourcePath),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, aligner.Requests.Count);

            // First call aligns the TTS take against the TRANSLATED text.
            var ttsRequest = aligner.Requests[0];
            Assert.Equal("hola mundo", ttsRequest.NormalizedTranscript);

            // Second call aligns the source clip against the ORIGINAL text.
            var sourceRequest = aligner.Requests[1];
            Assert.Equal("hello world", sourceRequest.NormalizedTranscript);
            Assert.StartsWith("src-", sourceRequest.SegmentId, StringComparison.Ordinal);

            // Lip-sync is a phoneme stage: both calls must demand phoneme timings and
            // carry the caller's preferred model alias for routing.
            foreach (var alignmentRequest in aligner.Requests)
            {
                Assert.True(alignmentRequest.Options.RequirePhonemeTimings);
                Assert.Equal("wav2vec2-lv60-espeak-cv-ft-onnx", alignmentRequest.Options.PreferredModelAlias);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Transcript split: missing source-language transcript → Partial, no clip
    // extraction, original TTS artifact untouched.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenSourceTranscriptMissing_PartialAndOriginalTtsPreserved()
    {
        string directory = CreateTempDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = MakeMediaAsset(projectId);
            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();

            var phonemes = new List<PhonemeTiming>
            {
                new("AH", "arpabet", TimeSpan.Zero, TimeSpan.FromMilliseconds(100), 0.9)
            };
            var aligner = new FakeForcedAligner
            {
                StatusToReturn = ForcedAlignmentStatus.Success,
                OverallConfidence = 0.95,
                PhonesToReturn = phonemes
            };
            var stretchService = new FakePhonemeStretchService();
            var clipExtractor = new FakeAudioClipExtractor();

            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath);
            var artifactId = Guid.NewGuid();
            var translatedSegmentId = Guid.NewGuid();

            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: translatedSegmentId) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            var sourceTiming = new Dictionary<Guid, SegmentSourceTiming>
            {
                [translatedSegmentId] = new(1.0, 4.0)
            };
            string fakeSourcePath = Path.Combine(directory, "source.wav");
            File.WriteAllBytes(fakeSourcePath, []);

            var handler = MakeHandler(artifactStore, stageRunStore, aligner, stretchService, clipExtractor);

            // SourceSegmentTranscriptMap deliberately omitted.
            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact],
                    SegmentTranscriptMap: new Dictionary<Guid, string> { [translatedSegmentId] = "hola mundo" },
                    SegmentSourceTimingMap: sourceTiming,
                    SourceAudioPath: fakeSourcePath),
                TestContext.Current.CancellationToken);

            var seg = result.Segments[0];
            Assert.Equal(LipSyncSegmentStatus.Partial, seg.Status);
            Assert.NotNull(seg.SkipReason);
            Assert.Contains("Source-language transcript not available", seg.SkipReason, StringComparison.Ordinal);

            // No source clip extraction, no stretch, single (TTS) alignment call.
            Assert.Equal(0, clipExtractor.CallCount);
            Assert.Equal(0, stretchService.CallCount);
            Assert.Equal(1, aligner.CallCount);

            // Original TTS artifact remains on disk untouched.
            Assert.True(File.Exists(artifactStore.GetPath(relPath)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
