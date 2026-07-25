using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class TranslationOrchestrationServiceTests
{
    [Fact]
    [Trait("Bug", "B-2")]
    public async Task RetranslateSegmentAsync_WhenEngineThrowsOce_MarksStageAsCanceled_NotFailed()
    {
        // Arrange
        var stageRunStore = new FakeProjectStageRunStore();
        var translationRepository = new FakeTranslationRepository();
        var glossaryRepository = new FakeGlossaryRepository();
        var ttsTakeRepository = new FakeTtsTakeRepository();
        var artifactStore = new FakeArtifactStore();
        var fileFingerprintService = new FakeFileFingerprintService();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var artifactWriter = new TranscriptArtifactWriter(
            artifactStore,
            fileFingerprintService,
            mediaAssetRepository);

        var cancellingEngine = new CancellingTranslationEngine();

        var service = new TranslationOrchestrationService(
            translationRepository,
            new GlossaryService(glossaryRepository),
            new GlossaryTermMatcher(),
            new FakeTranslationLanguageRouter(),
            cancellingEngine,
            ttsTakeRepository,
            stageRunStore,
            artifactStore,
            artifactWriter);

        TranscriptProjectState state = BuildStateWithTranslationRevision();

        RetranslateSegmentRequest request = new(
            state.CurrentTranslationRevision!.Id,
            state.TranscriptSegments[0].Id,
            SourceLanguage: "en",
            TargetLanguage: "es");

        // Act
        OperationCanceledException? caught = null;
        try
        {
            await service.RetranslateSegmentAsync(
                state,
                request,
                TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            caught = ex;
        }

        // Assert: the exception must propagate as OperationCanceledException
        Assert.NotNull(caught);

        // Assert: the stage run must be marked Canceled, not Failed
        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.Translation, stageRun.StageName);
        Assert.Equal(StageRunStatus.Canceled, stageRun.Status);
    }

    [Fact]
    public async Task GenerateTranslationAsync_WithAutoSource_FollowsPersistedTranscriptLanguage()
    {
        TranslationHarness harness = CreateTranslationHarness(transcriptLanguage: "en", segmentDetectedLanguage: null);

        await harness.Service.GenerateTranslationAsync(
            harness.State,
            new GenerateTranslationRequest(SourceLanguage: "auto", TargetLanguage: "es"),
            TestContext.Current.CancellationToken);

        Assert.Equal("en", harness.CapturedSourceLanguage());
        StageRunRecord stageRun = Assert.Single(harness.StageRunStore.All);
        Assert.Equal(StageRunStatus.Completed, stageRun.Status);
    }

    [Fact]
    public async Task GenerateTranslationAsync_WithAutoSource_FallsBackToDetectedSegmentLanguage_AndPersistsIt()
    {
        TranslationHarness harness = CreateTranslationHarness(transcriptLanguage: null, segmentDetectedLanguage: "en");

        await harness.Service.GenerateTranslationAsync(
            harness.State,
            new GenerateTranslationRequest(SourceLanguage: "auto", TargetLanguage: "es"),
            TestContext.Current.CancellationToken);

        Assert.Equal("en", harness.CapturedSourceLanguage());
        ProjectManifest? manifest = await harness.ArtifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            TestContext.Current.CancellationToken);
        Assert.Equal("en", manifest?.TranscriptLanguage);
    }

    [Fact]
    public async Task GenerateTranslationAsync_WithAutoSource_AndNoLanguageAnywhere_ThrowsActionableError()
    {
        TranslationHarness harness = CreateTranslationHarness(transcriptLanguage: null, segmentDetectedLanguage: null);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.GenerateTranslationAsync(
                harness.State,
                new GenerateTranslationRequest(SourceLanguage: "auto", TargetLanguage: "es"),
                TestContext.Current.CancellationToken));

        Assert.Contains("Source language is unknown", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.StageRunStore.All);
    }

    [Fact]
    public async Task GenerateTranslationAsync_WithEmptyTranscriptSegments_ThrowsInsteadOfNoOpSuccess()
    {
        TranslationHarness harness = CreateTranslationHarness(transcriptLanguage: "en", segmentDetectedLanguage: "en");
        TranscriptProjectState emptyState = harness.State with { TranscriptSegments = [] };

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.GenerateTranslationAsync(
                emptyState,
                new GenerateTranslationRequest(SourceLanguage: "auto", TargetLanguage: "es"),
                TestContext.Current.CancellationToken));

        Assert.Contains("no segments to translate", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.StageRunStore.All);
    }

    [Fact]
    public async Task GenerateTranslationAsync_WithExplicitSource_MismatchingTranscriptLanguage_Throws()
    {
        TranslationHarness harness = CreateTranslationHarness(transcriptLanguage: "en", segmentDetectedLanguage: "en");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.GenerateTranslationAsync(
                harness.State,
                new GenerateTranslationRequest(SourceLanguage: "fr", TargetLanguage: "es"),
                TestContext.Current.CancellationToken));

        Assert.Contains("does not match the transcript language", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.StageRunStore.All);
    }

    [Fact]
    public async Task GenerateTranslationAsync_WithExplicitSource_AdoptsItWhenNoTranscriptLanguageIsSet()
    {
        TranslationHarness harness = CreateTranslationHarness(transcriptLanguage: null, segmentDetectedLanguage: null);

        await harness.Service.GenerateTranslationAsync(
            harness.State,
            new GenerateTranslationRequest(SourceLanguage: "en", TargetLanguage: "es"),
            TestContext.Current.CancellationToken);

        Assert.Equal("en", harness.CapturedSourceLanguage());
        ProjectManifest? manifest = await harness.ArtifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            TestContext.Current.CancellationToken);
        Assert.Equal("en", manifest?.TranscriptLanguage);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed record TranslationHarness(
        TranslationOrchestrationService Service,
        TranscriptProjectState State,
        FakeProjectStageRunStore StageRunStore,
        FakeArtifactStore ArtifactStore,
        Func<string?> CapturedSourceLanguage);

    private static TranslationHarness CreateTranslationHarness(
        string? transcriptLanguage,
        string? segmentDetectedLanguage)
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var translationRepository = new FakeTranslationRepository();
        var glossaryRepository = new FakeGlossaryRepository();
        var ttsTakeRepository = new FakeTtsTakeRepository();
        var artifactStore = new FakeArtifactStore();
        var fileFingerprintService = new FakeFileFingerprintService();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var artifactWriter = new TranscriptArtifactWriter(
            artifactStore,
            fileFingerprintService,
            mediaAssetRepository);

        string? capturedSourceLanguage = null;
        var engine = new FakeTranslationEngine(
            (request, segment) =>
            {
                capturedSourceLanguage = request.SourceLanguage;
                return $"[es] {segment.Text}";
            });

        var service = new TranslationOrchestrationService(
            translationRepository,
            new GlossaryService(glossaryRepository),
            new GlossaryTermMatcher(),
            new FakeTranslationLanguageRouter(),
            engine,
            ttsTakeRepository,
            stageRunStore,
            artifactStore,
            artifactWriter);

        TranscriptProjectState state = BuildStateWithTranslationRevision(transcriptLanguage, segmentDetectedLanguage);
        return new TranslationHarness(service, state, stageRunStore, artifactStore, () => capturedSourceLanguage);
    }

    private static TranscriptProjectState BuildStateWithTranslationRevision(
        string? transcriptLanguage = "en",
        string? segmentDetectedLanguage = "en")
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid projectId = Guid.NewGuid();
        var project = new TrackdubProject(projectId, "Test Project", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            4.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var projectState = new OpenProjectResult(
            project,
            mediaAsset,
            null,
            SourceMediaStatus.Available,
            null,
            [],
            transcriptLanguage);

        TranscriptRevision transcriptRevision = TranscriptRevision.Create(
            projectId,
            stageRunId: null,
            revisionNumber: 1,
            now);

        var speaker = new ProjectSpeaker(Guid.NewGuid(), projectId, "Speaker 1", now);

        TranscriptSegment[] transcriptSegments =
        [
            TranscriptSegment.Create(
                transcriptRevision.Id,
                0,
                0.0d,
                2.0d,
                "Hello there.",
                speaker.Id,
                segmentDetectedLanguage)
        ];

        TranslationRevision translationRevision = TranslationRevision.Create(
            projectId,
            stageRunId: null,
            transcriptRevision.Id,
            "es",
            revisionNumber: 1,
            now,
            translationProvider: "fake",
            modelId: "fake-model");

        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            translationRevision.Id,
            0,
            0.0d,
            2.0d,
            "Hola.");

        return new TranscriptProjectState(
            projectState,
            transcriptRevision,
            transcriptSegments,
            [speaker],
            [],
            translationRevision,
            [translatedSegment],
            IsTranslationStale: false,
            TranscriptLanguage: transcriptLanguage,
            StageRuns: [],
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: "es",
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: [],
            VoiceAssignments: [],
            TtsTakes: [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);
    }

    // -------------------------------------------------------------------------
    // Private test doubles
    // -------------------------------------------------------------------------

    /// <summary>
    /// A translation engine that immediately throws <see cref="OperationCanceledException"/>
    /// to simulate the engine being cancelled mid-run.
    /// </summary>
    private sealed class CancellingTranslationEngine : ITranslationEngine
    {
        public Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException("Translation was cancelled.");
    }
}
