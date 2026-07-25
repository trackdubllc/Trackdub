using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

/// <summary>
/// Direct unit tests for <see cref="SpeakerAssignmentService"/> — static helpers and
/// <see cref="SpeakerAssignmentService.CreateDefaultSpeakerAssignmentAsync"/> / <see cref="SpeakerAssignmentService.CreateDiarizationAsync"/>.
/// Tests are grouped by method.  Each test uses only the deps that the method under test actually touches.
/// </summary>
public sealed class SpeakerAssignmentServiceTests
{
    private readonly Guid projectId = Guid.NewGuid();
    private readonly Guid mediaAssetId = Guid.NewGuid();

    // ─────────────────────────────────────────────────────────────────────────
    // AssignTranscriptSegmentsToSpeakers  (static — no service construction)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AssignTranscriptSegmentsToSpeakers_FullOverlap_AssignsMatchingTurnSpeaker()
    {
        var speakerA = ProjectSpeaker.Create(projectId, "Speaker A", DateTimeOffset.UtcNow);
        var speakerB = ProjectSpeaker.Create(projectId, "Speaker B", DateTimeOffset.UtcNow.AddMilliseconds(1));

        var turnA = SpeakerTurn.Create(projectId, speakerA.Id, 0.0, 5.0);
        var turnB = SpeakerTurn.Create(projectId, speakerB.Id, 5.0, 10.0);

        var segments = new[]
        {
            new RecognizedTranscriptSegment(0, 0.5, 3.0, "Hello"),
            new RecognizedTranscriptSegment(1, 5.5, 8.0, "World"),
        };

        Dictionary<int, Guid> result = SpeakerAssignmentService.AssignTranscriptSegmentsToSpeakers(
            segments, [speakerA, speakerB], [turnA, turnB]);

        Assert.Equal(speakerA.Id, result[0]);
        Assert.Equal(speakerB.Id, result[1]);
    }

    [Fact]
    public void AssignTranscriptSegmentsToSpeakers_GreaterOverlapWins()
    {
        // Segment spans 0-10.  Turn A covers 0-6 (6 s overlap), Turn B covers 5-10 (5 s overlap).
        // A should win.
        var speakerA = ProjectSpeaker.Create(projectId, "Speaker A", DateTimeOffset.UtcNow);
        var speakerB = ProjectSpeaker.Create(projectId, "Speaker B", DateTimeOffset.UtcNow.AddMilliseconds(1));

        var turnA = SpeakerTurn.Create(projectId, speakerA.Id, 0.0, 6.0);
        var turnB = SpeakerTurn.Create(projectId, speakerB.Id, 5.0, 10.0);

        var segment = new RecognizedTranscriptSegment(0, 0.0, 10.0, "Sentence");

        Dictionary<int, Guid> result = SpeakerAssignmentService.AssignTranscriptSegmentsToSpeakers(
            [segment], [speakerA, speakerB], [turnA, turnB]);

        Assert.Equal(speakerA.Id, result[0]);
    }

    [Fact]
    public void AssignTranscriptSegmentsToSpeakers_EqualOverlap_HigherConfidenceWins()
    {
        // Both turns overlap the segment by 3 seconds; B has higher confidence.
        var speakerA = ProjectSpeaker.Create(projectId, "Speaker A", DateTimeOffset.UtcNow);
        var speakerB = ProjectSpeaker.Create(projectId, "Speaker B", DateTimeOffset.UtcNow.AddMilliseconds(1));

        var turnA = SpeakerTurn.Create(projectId, speakerA.Id, 0.0, 5.0, confidence: 0.70);
        var turnB = SpeakerTurn.Create(projectId, speakerB.Id, 2.0, 7.0, confidence: 0.95);

        // Segment 2-5: overlap with A = 3 s, overlap with B = 3 s → confidence tie-break
        var segment = new RecognizedTranscriptSegment(0, 2.0, 5.0, "Text");

        Dictionary<int, Guid> result = SpeakerAssignmentService.AssignTranscriptSegmentsToSpeakers(
            [segment], [speakerA, speakerB], [turnA, turnB]);

        Assert.Equal(speakerB.Id, result[0]);
    }

    [Fact]
    public void AssignTranscriptSegmentsToSpeakers_NoOverlap_FallsBackToEarliestCreatedSpeaker()
    {
        var earlier = ProjectSpeaker.Create(projectId, "Early Speaker", DateTimeOffset.UtcNow.AddSeconds(-5));
        var later = ProjectSpeaker.Create(projectId, "Late Speaker", DateTimeOffset.UtcNow);

        // Turn is way outside the segment
        var turn = SpeakerTurn.Create(projectId, later.Id, 100.0, 200.0);

        var segment = new RecognizedTranscriptSegment(0, 0.0, 5.0, "No overlap");

        Dictionary<int, Guid> result = SpeakerAssignmentService.AssignTranscriptSegmentsToSpeakers(
            [segment], [earlier, later], [turn]);

        Assert.Equal(earlier.Id, result[0]);
    }

    [Fact]
    public void AssignTranscriptSegmentsToSpeakers_EmptySegments_ReturnsEmptyDictionary()
    {
        var speaker = ProjectSpeaker.Create(projectId, "Speaker", DateTimeOffset.UtcNow);
        var turn = SpeakerTurn.Create(projectId, speaker.Id, 0.0, 5.0);

        Dictionary<int, Guid> result = SpeakerAssignmentService.AssignTranscriptSegmentsToSpeakers(
            [], [speaker], [turn]);

        Assert.Empty(result);
    }

    [Fact]
    public void AssignTranscriptSegmentsToSpeakers_NoSpeakersAndNoTurns_ReturnsEmptyDictionary()
    {
        var segment = new RecognizedTranscriptSegment(0, 0.0, 5.0, "Orphan");

        Dictionary<int, Guid> result = SpeakerAssignmentService.AssignTranscriptSegmentsToSpeakers(
            [segment], [], []);

        // No fallback speaker → segment not mapped
        Assert.Empty(result);
    }

    [Fact]
    public void AssignTranscriptSegmentsToSpeakers_MultipleSegments_AllMapped()
    {
        var speakerA = ProjectSpeaker.Create(projectId, "A", DateTimeOffset.UtcNow);
        var speakerB = ProjectSpeaker.Create(projectId, "B", DateTimeOffset.UtcNow.AddMilliseconds(1));

        var turnA = SpeakerTurn.Create(projectId, speakerA.Id, 0.0, 6.0);
        var turnB = SpeakerTurn.Create(projectId, speakerB.Id, 6.0, 12.0);

        var segments = new[]
        {
            new RecognizedTranscriptSegment(0, 0.0, 2.0, "First"),
            new RecognizedTranscriptSegment(1, 3.0, 5.5, "Second"),
            new RecognizedTranscriptSegment(2, 7.0, 9.0, "Third"),
        };

        Dictionary<int, Guid> result = SpeakerAssignmentService.AssignTranscriptSegmentsToSpeakers(
            segments, [speakerA, speakerB], [turnA, turnB]);

        Assert.Equal(3, result.Count);
        Assert.Equal(speakerA.Id, result[0]);
        Assert.Equal(speakerA.Id, result[1]);
        Assert.Equal(speakerB.Id, result[2]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CreateDefaultSpeakerAssignment  (static)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateDefaultSpeakerAssignment_AllSegmentsMappedToSingleSpeaker()
    {
        var speaker = ProjectSpeaker.Create(projectId, "Default", DateTimeOffset.UtcNow);
        var segments = new[]
        {
            new RecognizedTranscriptSegment(0, 0.0, 2.0, "Hello"),
            new RecognizedTranscriptSegment(1, 3.0, 5.0, "World"),
        };

        SpeakerAssignmentResult result = SpeakerAssignmentService.CreateDefaultSpeakerAssignment(speaker, segments);

        Assert.Equal(speaker, Assert.Single(result.Speakers));
        Assert.Empty(result.Turns);
        Assert.Equal(speaker.Id, result.SegmentSpeakerIdsByIndex[0]);
        Assert.Equal(speaker.Id, result.SegmentSpeakerIdsByIndex[1]);
    }

    [Fact]
    public void CreateDefaultSpeakerAssignment_EmptySegments_EmptyMapping()
    {
        var speaker = ProjectSpeaker.Create(projectId, "Default", DateTimeOffset.UtcNow);

        SpeakerAssignmentResult result = SpeakerAssignmentService.CreateDefaultSpeakerAssignment(speaker, []);

        Assert.Equal(speaker, Assert.Single(result.Speakers));
        Assert.Empty(result.SegmentSpeakerIdsByIndex);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CreateDefaultSpeakerAssignmentAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDefaultSpeakerAssignmentAsync_NoExistingSpeaker_CreatesDefaultSpeaker()
    {
        SpeakerAssignmentService svc = BuildService();
        var segments = new[]
        {
            new RecognizedTranscriptSegment(0, 0.0, 2.0, "Hi"),
            new RecognizedTranscriptSegment(1, 3.0, 5.0, "There"),
        };

        SpeakerAssignmentResult result = await svc.CreateDefaultSpeakerAssignmentAsync(
            projectId, segments, CancellationToken.None);

        Assert.Single(result.Speakers);
        Assert.Equal(projectId, result.Speakers[0].ProjectId);
        Assert.Equal(result.Speakers[0].Id, result.SegmentSpeakerIdsByIndex[0]);
        Assert.Equal(result.Speakers[0].Id, result.SegmentSpeakerIdsByIndex[1]);
    }

    [Fact]
    public async Task CreateDefaultSpeakerAssignmentAsync_ExistingSpeaker_ReusesExistingSpeaker()
    {
        var speaker = ProjectSpeaker.Create(projectId, "Existing", DateTimeOffset.UtcNow);
        speakerRepository.Seed([speaker]);

        SpeakerAssignmentService svc = BuildService();
        var segments = new[] { new RecognizedTranscriptSegment(0, 0.0, 2.0, "Hello") };

        SpeakerAssignmentResult result = await svc.CreateDefaultSpeakerAssignmentAsync(
            projectId, segments, CancellationToken.None);

        Assert.Equal(speaker.Id, result.Speakers[0].Id);
        Assert.Equal(speaker.Id, result.SegmentSpeakerIdsByIndex[0]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CreateDiarizationAsync — error/edge paths
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDiarizationAsync_NoTurnsFromEngine_ReturnsNullAndCompletesStageRun()
    {
        diarizationEngine.SetTurns([]);
        SpeakerAssignmentService svc = BuildService();

        DiarizationResult? result = await svc.CreateDiarizationAsync(
            projectId,
            mediaAssetId,
            normalizedAudioPath: "/fake/audio.wav",
            durationSeconds: 30.0,
            regions: [],
            preferredModelAlias: null,
            cancellationToken: CancellationToken.None);

        Assert.Null(result);

        // Stage run should have been started and then completed (not failed/cancelled)
        StageRunRecord? stageRun = stageRunStore.All.SingleOrDefault();
        Assert.NotNull(stageRun);
        Assert.Equal(StageNames.Diarization, stageRun.StageName);
        Assert.Equal(StageRunStatus.Completed, stageRun.Status);
    }

    [Fact]
    public async Task CreateDiarizationAsync_EngineThrows_ReturnsNullAndFailsStageRun()
    {
        diarizationEngine.ThrowOnNext(new InvalidOperationException("Model not loaded"));
        SpeakerAssignmentService svc = BuildService();

        DiarizationResult? result = await svc.CreateDiarizationAsync(
            projectId,
            mediaAssetId,
            normalizedAudioPath: "/fake/audio.wav",
            durationSeconds: 30.0,
            regions: [],
            preferredModelAlias: null,
            cancellationToken: CancellationToken.None);

        Assert.Null(result);

        StageRunRecord? stageRun = stageRunStore.All.SingleOrDefault();
        Assert.NotNull(stageRun);
        Assert.Equal(StageNames.Diarization, stageRun.StageName);
        Assert.Equal(StageRunStatus.Failed, stageRun.Status);
        Assert.Contains("Model not loaded", stageRun.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDiarizationAsync_Cancelled_CancelsStageRunAndRethrows()
    {
        using var cts = new CancellationTokenSource();
        diarizationEngine.ThrowOnNext(new OperationCanceledException(cts.Token));
        SpeakerAssignmentService svc = BuildService();

        await Assert.ThrowsAsync<OperationCanceledException>(() => svc.CreateDiarizationAsync(
            projectId,
            mediaAssetId,
            normalizedAudioPath: "/fake/audio.wav",
            durationSeconds: 30.0,
            regions: [],
            preferredModelAlias: null,
            cancellationToken: CancellationToken.None));

        StageRunRecord? stageRun = stageRunStore.All.SingleOrDefault();
        Assert.NotNull(stageRun);
        Assert.Equal(StageNames.Diarization, stageRun.StageName);
        Assert.Equal(StageRunStatus.Canceled, stageRun.Status);
    }

    [Fact]
    public async Task CreateDiarizationAsync_ValidTurns_ReturnsSpeakersAndPersistsThem()
    {
        // Use the default fixture turns (spk_0 and spk_1)
        SpeakerAssignmentService svc = BuildService();

        DiarizationResult? result = await svc.CreateDiarizationAsync(
            projectId,
            mediaAssetId,
            normalizedAudioPath: "/fake/audio.wav",
            durationSeconds: 30.0,
            regions: [],
            preferredModelAlias: null,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Speakers.Count);
        Assert.Equal(2, result.Turns.Count);

        // Speakers should be persisted
        Assert.Equal(2, speakerRepository.Speakers.Count(s => s.ProjectId == projectId));

        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.Diarization, stageRun.StageName);
        Assert.Equal(StageRunStatus.Completed, stageRun.Status);
        Assert.All(result.Turns, turn => Assert.Equal(stageRun.Id, turn.StageRunId));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private readonly FakeSpeakerRepository speakerRepository = new();
    private readonly FakeProjectStageRunStore stageRunStore = new();
    private readonly ConfigurableFakeDiarizationEngine diarizationEngine = new();
    private readonly FakeTranscriptRepository transcriptRepository = new();
    private readonly FakeTtsTakeRepository ttsTakeRepository = new();
    private readonly FakeArtifactStore artifactStore = new();
    private readonly FakeFileFingerprintService fingerprintService = new();
    private readonly FakeMediaAssetRepository mediaAssetRepository = new();
    private readonly FakeVoiceAssignmentRepository voiceAssignmentRepository = new();
    private readonly FakeReferenceClipAnalyzer referenceClipAnalyzer = new();
    private readonly FakeReferenceClipTrimmer referenceClipTrimmer = new();

    private TranscriptArtifactWriter BuildArtifactWriter() =>
        new(artifactStore, fingerprintService, mediaAssetRepository);

    private SpeakerReferenceClipService BuildReferenceClipService() =>
        new(artifactStore,
            new FakeAudioClipExtractor(),
            fingerprintService,
            mediaAssetRepository,
            voiceAssignmentRepository,
            ttsTakeRepository,
            referenceClipAnalyzer,
            referenceClipTrimmer);

    private SpeakerAssignmentService BuildService() =>
        new(speakerRepository,
            transcriptRepository,
            new SegmentEditingService(transcriptRepository, ttsTakeRepository, BuildArtifactWriter()),
            artifactStore,
            stageRunStore,
            diarizationEngine,
            BuildReferenceClipService(),
            BuildArtifactWriter(),
            new DiarizationStageHandler(
                diarizationEngine,
                new WritingModelDownloader(),
                modelCacheRoot: Path.Combine(Path.GetTempPath(), "trackdub-tests", Guid.NewGuid().ToString("N")),
                expectedSha256: SortFormerTestFixtures.ExpectedSha256));

    /// <summary>
    /// A configurable wrapper that lets individual tests override the returned turns or inject an exception.
    /// </summary>
    private sealed class ConfigurableFakeDiarizationEngine : ISpeakerDiarizationEngine, IStageRuntimeExecutionReporter
    {
        private IReadOnlyList<DiarizedSpeakerTurn>? overrideTurns;
        private Exception? nextException;

        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

        public void SetTurns(IReadOnlyList<DiarizedSpeakerTurn> turns) => overrideTurns = turns;

        public void ThrowOnNext(Exception ex) => nextException = ex;

        public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
            string normalizedAudioPath,
            double durationSeconds,
            IReadOnlyList<SpeechRegion> speechRegions,
            CancellationToken cancellationToken)
        {
            if (nextException is not null)
            {
                Exception ex = nextException;
                nextException = null;
                throw ex;
            }

            LastExecutionSummary = new StageRuntimeExecutionSummary(
                "auto", "cpu", "fake/sortformer", "sortformer-diarizer-4spk-v2.1", "default",
                "Configurable fake diarization");

            if (overrideTurns is not null)
            {
                return Task.FromResult(overrideTurns);
            }

            IReadOnlyList<DiarizedSpeakerTurn> fixtureTurns = new List<DiarizedSpeakerTurn>
            {
                new("spk_0", 0.0, 5.8, 0.93, false),
                new("spk_1", 6.0, 11.8, 0.88, true),
            };
            return Task.FromResult(fixtureTurns);
        }
    }

    private sealed class FakeAudioClipExtractor : IAudioClipExtractor
    {
        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            double startSeconds,
            double endSeconds,
            string destinationPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AudioClipExtractionResult(destinationPath, 0.0, 16000, 1));

        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            IReadOnlyList<AudioClipRange> ranges,
            string destinationPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AudioClipExtractionResult(destinationPath, 0.0, 16000, 1));
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
