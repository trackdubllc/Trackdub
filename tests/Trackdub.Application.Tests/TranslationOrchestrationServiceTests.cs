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

        // The engine cancels the caller's token, then throws — a real user
        // cancellation arriving mid-call.
        using var cts = new CancellationTokenSource();
        var cancellingEngine = new CancellingTranslationEngine(cts);

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
                cts.Token);
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
    public async Task RetranslateSegmentAsync_WhenEngineTimesOutWithoutUserCancel_MarksStageAsFailed()
    {
        // A provider-side cancellation (e.g. HttpClient timeout) is not a user
        // cancel: the stage run must record Failed, not Canceled.
        var stageRunStore = new FakeProjectStageRunStore();
        var artifactStore = new FakeArtifactStore();
        var service = new TranslationOrchestrationService(
            new FakeTranslationRepository(),
            new GlossaryService(new FakeGlossaryRepository()),
            new GlossaryTermMatcher(),
            new FakeTranslationLanguageRouter(),
            new TimeoutTranslationEngine(),
            new FakeTtsTakeRepository(),
            stageRunStore,
            artifactStore,
            new TranscriptArtifactWriter(
                artifactStore,
                new FakeFileFingerprintService(),
                new FakeMediaAssetRepository()));

        TranscriptProjectState state = BuildStateWithTranslationRevision();
        RetranslateSegmentRequest request = new(
            state.CurrentTranslationRevision!.Id,
            state.TranscriptSegments[0].Id,
            SourceLanguage: "en",
            TargetLanguage: "es");

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            service.RetranslateSegmentAsync(
                state,
                request,
                TestContext.Current.CancellationToken));

        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.Translation, stageRun.StageName);
        Assert.Equal(StageRunStatus.Failed, stageRun.Status);
    }

    [Fact]
    public async Task RetranslateSegmentAsync_WhenEngineThrowsBareOceWithoutCancel_MarksStageAsFailed()
    {
        // A bare OperationCanceledException (not TaskCanceledException) without
        // canceling the caller's token is a provider-side failure: must record Failed.
        var stageRunStore = new FakeProjectStageRunStore();
        var artifactStore = new FakeArtifactStore();
        var service = new TranslationOrchestrationService(
            new FakeTranslationRepository(),
            new GlossaryService(new FakeGlossaryRepository()),
            new GlossaryTermMatcher(),
            new FakeTranslationLanguageRouter(),
            new BareOceTranslationEngine(),
            new FakeTtsTakeRepository(),
            stageRunStore,
            artifactStore,
            new TranscriptArtifactWriter(
                artifactStore,
                new FakeFileFingerprintService(),
                new FakeMediaAssetRepository()));

        TranscriptProjectState state = BuildStateWithTranslationRevision();
        RetranslateSegmentRequest request = new(
            state.CurrentTranslationRevision!.Id,
            state.TranscriptSegments[0].Id,
            SourceLanguage: "en",
            TargetLanguage: "es");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RetranslateSegmentAsync(state, request, TestContext.Current.CancellationToken));

        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.Translation, stageRun.StageName);
        Assert.Equal(StageRunStatus.Failed, stageRun.Status);
    }

    [Fact]
    public async Task GenerateTranslationAsync_WhenEngineThrowsOceUnderCancelledToken_MarksStageAsCanceled()
    {
        using var cts = new CancellationTokenSource();
        TranslationHarness harness = CreateTranslationHarness(
            transcriptLanguage: "en",
            segmentDetectedLanguage: "en",
            engine: new CancellingTranslationEngine(cts));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            harness.Service.GenerateTranslationAsync(
                harness.State,
                new GenerateTranslationRequest(SourceLanguage: "auto", TargetLanguage: "es"),
                cts.Token));

        StageRunRecord stageRun = Assert.Single(harness.StageRunStore.All);
        Assert.Equal(StageRunStatus.Canceled, stageRun.Status);
    }

    [Fact]
    public async Task GenerateTranslationAsync_WhenEngineTimesOutWithoutUserCancel_MarksStageAsFailed()
    {
        TranslationHarness harness = CreateTranslationHarness(
            transcriptLanguage: "en",
            segmentDetectedLanguage: "en",
            engine: new TimeoutTranslationEngine());

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            harness.Service.GenerateTranslationAsync(
                harness.State,
                new GenerateTranslationRequest(SourceLanguage: "auto", TargetLanguage: "es"),
                TestContext.Current.CancellationToken));

        StageRunRecord stageRun = Assert.Single(harness.StageRunStore.All);
        Assert.Equal(StageRunStatus.Failed, stageRun.Status);
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

    [Fact]
    public async Task GenerateTranslationAsync_WhenArtifactPersistFails_MarksStageFailedNotCompleted()
    {
        TranslationHarness harness = CreateTranslationHarness(
            transcriptLanguage: "en",
            segmentDetectedLanguage: "en");
        // The fresh repository starts empty, so the new revision lands at
        // artifacts/translation/es/translation-revision-0001.json.
        harness.ArtifactStore.FailingJsonWriteFileName = "translation-revision-0001.json";

        await Assert.ThrowsAsync<IOException>(() =>
            harness.Service.GenerateTranslationAsync(
                harness.State,
                new GenerateTranslationRequest(SourceLanguage: "auto", TargetLanguage: "es"),
                TestContext.Current.CancellationToken));

        StageRunRecord stageRun = Assert.Single(harness.StageRunStore.All);
        Assert.Equal(StageRunStatus.Failed, stageRun.Status);
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
        string? segmentDetectedLanguage,
        ITranslationEngine? engine = null)
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
        engine ??= new FakeTranslationEngine(
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
    /// A translation engine that cancels the caller's token and then throws
    /// <see cref="OperationCanceledException"/>, simulating user cancellation
    /// arriving mid-run.
    /// </summary>
    private sealed class CancellingTranslationEngine(CancellationTokenSource cts) : ITranslationEngine
    {
        public Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            cts.Cancel();
            throw new OperationCanceledException("Translation was cancelled.");
        }
    }

    /// <summary>
    /// A translation engine that throws <see cref="TaskCanceledException"/>
    /// without touching the caller's token, simulating a cloud-provider timeout.
    /// </summary>
    private sealed class TimeoutTranslationEngine : ITranslationEngine
    {
        public Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken) =>
            throw new TaskCanceledException("The request timed out.");
    }

    /// <summary>
    /// A translation engine that throws a bare <see cref="OperationCanceledException"/>
    /// without canceling the caller's token, simulating a provider-internal failure.
    /// </summary>
    private sealed class BareOceTranslationEngine : ITranslationEngine
    {
        public Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException("Provider-side operation was cancelled.");
    }
}
