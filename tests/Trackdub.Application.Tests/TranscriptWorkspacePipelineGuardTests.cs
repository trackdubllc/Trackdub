using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Application.Mixing;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Application.Transcripts.Stages;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Projects;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class TranscriptWorkspacePipelineGuardTests
{
    [Fact]
    public async Task RunPipelineAsync_rejects_concurrent_attempt()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.RunPipelineAsync(
                "SecondOperation",
                static _ => Task.FromResult(2),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task RunPipelineAsync_releases_guard_after_success()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();

        int first = await workspace.RunPipelineAsync(
            "FirstOperation",
            static _ => Task.FromResult(1),
            TestContext.Current.CancellationToken);
        int second = await workspace.RunPipelineAsync(
            "SecondOperation",
            static _ => Task.FromResult(2),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task RunPipelineAsync_releases_guard_after_cancellation()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        using var cancellation = new CancellationTokenSource();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "CancelableOperation",
            blocking.RunAsync,
            cancellation.Token);
        await blocking.WaitForStartAsync();

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        int second = await workspace.RunPipelineAsync(
            "SecondOperation",
            static _ => Task.FromResult(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task RunPipelineAsync_releases_guard_after_fault()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.RunPipelineAsync<int>(
                "FaultingOperation",
                static _ => Task.FromException<int>(new InvalidOperationException("boom")),
                TestContext.Current.CancellationToken));

        int second = await workspace.RunPipelineAsync(
            "SecondOperation",
            static _ => Task.FromResult(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task RunPipelineAsync_logs_rejected_operation_name_and_reason()
    {
        var logger = new CapturingLogger();
        using TranscriptWorkspace workspace = CreateWorkspace(logger);
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.RunPipelineAsync(
                "RejectedOperation",
                static _ => Task.FromResult(2),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
        string warning = Assert.Single(logger.Warnings);
        Assert.Contains("pipeline_guard_busy", warning, StringComparison.Ordinal);
        Assert.Contains("RejectedOperation", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_open_is_not_blocked_while_guard_is_held()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        TranscriptProjectState state = await workspace.Project
            .OpenAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal("Guard Test", state.ProjectState.Project.Name);
        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task SaveProjectUiSettingsAsync_proceeds_concurrently_with_pipeline_work()
    {
        // SaveProjectUiSettingsAsync is a light write — it must not be blocked by the pipeline guard.
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await AssertNotBlockedByPipelineGuardAsync(() =>
            workspace.SaveProjectUiSettingsAsync(
                new ProjectUiSettings(new ProjectMixSettings(SourceGainDb: -3d)),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task RestoreEditingStateAsync_proceeds_concurrently_with_pipeline_work()
    {
        // RestoreEditingStateAsync is a light write — it must not be blocked by the pipeline guard.
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await AssertNotBlockedByPipelineGuardAsync(() =>
            workspace.RestoreEditingStateAsync(
                new RestoreEditingStateRequest("es", [], null, new Dictionary<Guid, string>(), []),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task RenameProjectAsync_proceeds_concurrently_with_pipeline_work()
    {
        // RenameProjectAsync is a light write — it must not be blocked by the pipeline guard.
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await AssertNotBlockedByPipelineGuardAsync(() =>
            workspace.RenameProjectAsync(
                new RenameProjectRequest("Renamed Guard Test", SelectedTranslationTargetLanguage: null),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public Task RelocateSourceAsync_is_guarded_against_concurrent_pipeline_work() =>
        AssertGuardedOperationRejectedAsync(workspace =>
            workspace.RelocateSourceAsync(
                new RelocateTranscriptSourceRequest(@"C:\media\relocated-source.mp4"),
                TestContext.Current.CancellationToken));

    [Fact]
    public Task SelectTranslationTargetAsync_proceeds_concurrently_with_pipeline_work() =>
        // SelectTranslationTargetAsync is a light write — must not be blocked by the pipeline guard.
        AssertNotBlockedByPipelineGuardAsync(workspace =>
            workspace.SelectTranslationTargetAsync(
                new SetTranslationTargetRequest("es"),
                TestContext.Current.CancellationToken));

    [Fact]
    public Task RenameSpeakerAsync_proceeds_concurrently_with_pipeline_work() =>
        // RenameSpeakerAsync is a light write — must not be blocked by the pipeline guard.
        AssertNotBlockedByPipelineGuardAsync(workspace =>
            workspace.RenameSpeakerAsync(
                new RenameSpeakerRequest(Guid.NewGuid(), "Renamed Speaker"),
                TestContext.Current.CancellationToken));

    [Fact]
    public Task MergeSpeakersAsync_proceeds_concurrently_with_pipeline_work() =>
        // MergeSpeakersAsync is a light write — must not be blocked by the pipeline guard.
        AssertNotBlockedByPipelineGuardAsync(workspace =>
            workspace.MergeSpeakersAsync(
                new MergeSpeakersRequest(Guid.NewGuid(), Guid.NewGuid()),
                TestContext.Current.CancellationToken));

    [Fact]
    public Task AssignVoiceToSpeakerAsync_is_guarded_against_concurrent_pipeline_work() =>
        AssertGuardedOperationRejectedAsync(workspace =>
            workspace.AssignVoiceToSpeakerAsync(
                new AssignVoiceToSpeakerRequest(Guid.NewGuid(), "voice-id"),
                TestContext.Current.CancellationToken));

    [Fact]
    public Task ExtractReferenceClipAsync_is_guarded_against_concurrent_pipeline_work() =>
        AssertGuardedOperationRejectedAsync(workspace =>
            workspace.ExtractReferenceClipAsync(
                new ExtractReferenceClipRequest(Guid.NewGuid(), Guid.NewGuid()),
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task TrimSegmentAsync_is_guarded_against_concurrent_pipeline_work()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.TrimSegmentAsync(
                new TrimTranscriptSegmentRequest(Guid.NewGuid(), Guid.NewGuid(), 0d, 1d),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task AssignSpeakerToSegmentAsync_is_guarded_against_concurrent_pipeline_work()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.AssignSpeakerToSegmentAsync(
                new AssignSpeakerToSegmentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task SplitSegmentAsync_is_guarded_against_concurrent_pipeline_work()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.SplitSegmentAsync(
                new SplitTranscriptSegmentRequest(Guid.NewGuid(), Guid.NewGuid(), 1d),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public Task MergeSegmentRunAsync_is_guarded_against_concurrent_pipeline_work() =>
        AssertGuardedOperationRejectedAsync(workspace =>
            workspace.MergeSegmentRunAsync(
                new MergeTranscriptSegmentRunRequest(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]),
                TestContext.Current.CancellationToken));

    [Fact]
    public Task DeleteSegmentAsync_is_guarded_against_concurrent_pipeline_work() =>
        AssertGuardedOperationRejectedAsync(workspace =>
            workspace.DeleteSegmentAsync(
                new DeleteTranscriptSegmentRequest(Guid.NewGuid(), Guid.NewGuid()),
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task GenerateTtsForAllSpeakersAsync_is_guarded_against_concurrent_pipeline_work()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.GenerateTtsForAllSpeakersAsync(
                new GenerateTtsForAllSpeakersRequest(),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public Task GenerateTtsForSegmentAsync_is_guarded_against_concurrent_pipeline_work() =>
        AssertGuardedOperationRejectedAsync(workspace =>
            workspace.GenerateTtsForSegmentAsync(
                new GenerateTtsForSegmentRequest(Guid.NewGuid(), Guid.NewGuid()),
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task GenerateTtsForSpeakerAsync_is_guarded_against_concurrent_pipeline_work()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.GenerateTtsForSpeakerAsync(
                new GenerateTtsForSpeakerRequest(Guid.NewGuid()),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task RegenerateStaleTtsForSpeakerAsync_is_guarded_against_concurrent_pipeline_work()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.RegenerateStaleTtsForSpeakerAsync(
                new RegenerateStaleTtsForSpeakerRequest(Guid.NewGuid()),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public Task AssignSpeakerToSegmentsAsync_is_guarded_against_concurrent_pipeline_work() =>
        AssertGuardedOperationRejectedAsync(workspace =>
            workspace.AssignSpeakerToSegmentsAsync(
                new AssignSpeakerToSegmentsRequest(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid()),
                TestContext.Current.CancellationToken));

    [Fact]
    public Task CreateSpeakerFromSegmentsAsync_is_guarded_against_concurrent_pipeline_work() =>
        AssertGuardedOperationRejectedAsync(workspace =>
            workspace.CreateSpeakerFromSegmentsAsync(
                new CreateSpeakerFromSegmentsRequest(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]),
                TestContext.Current.CancellationToken));

    [Fact]
    public Task SplitSpeakerTurnAsync_proceeds_concurrently_with_pipeline_work() =>
        // SplitSpeakerTurnAsync is a light write — must not be blocked by the pipeline guard.
        AssertNotBlockedByPipelineGuardAsync(workspace =>
            workspace.SplitSpeakerTurnAsync(
                new SplitSpeakerTurnRequest(Guid.NewGuid(), 1d),
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task StretchTtsTakeAsync_is_guarded_against_concurrent_pipeline_work()
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.StretchTtsTakeAsync(
                new StretchTtsTakeRequest(Guid.NewGuid()),
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task CreatePreviewMixAsync_is_guarded_before_project_reload()
    {
        var logger = new CapturingLogger();
        using TranscriptWorkspace workspace = CreateWorkspace(logger);
        TranscriptProjectState state = await workspace.Project.OpenAsync(TestContext.Current.CancellationToken);
        var blocking = new BlockingOperation();
        var commands = new TranscriptWorkspaceCommandService();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.CreatePreviewMixAsync(
                workspace,
                state,
                new PreviewMixStageRequest(state.ProjectState.Project.Id, 0d, 1d),
                selectedTranslationTargetLanguageCode: null,
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
        string warning = Assert.Single(logger.Warnings);
        Assert.Contains(nameof(TranscriptWorkspaceCommandService.CreatePreviewMixAsync), warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateExportAsync_is_guarded_before_project_reload()
    {
        var logger = new CapturingLogger();
        using TranscriptWorkspace workspace = CreateWorkspace(logger);
        TranscriptProjectState state = await workspace.Project.OpenAsync(TestContext.Current.CancellationToken);
        var blocking = new BlockingOperation();
        var commands = new TranscriptWorkspaceCommandService();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.CreateExportAsync(
                workspace,
                state,
                new ExportStageRequest(
                    state.ProjectState.Project.Id,
                    Path.Combine(Path.GetTempPath(), "guard-export.mp4"),
                    []),
                selectedTranslationTargetLanguageCode: null,
                TestContext.Current.CancellationToken));

        blocking.Release();
        Assert.Equal(1, await first);
        string warning = Assert.Single(logger.Warnings);
        Assert.Contains(nameof(TranscriptWorkspaceCommandService.CreateExportAsync), warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_cancels_in_flight_guarded_operation()
    {
        TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        workspace.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            workspace.RunPipelineAsync(
                "AfterDisposeOperation",
                static _ => Task.FromResult(2),
                TestContext.Current.CancellationToken));
    }

    private static TranscriptWorkspace CreateWorkspace(IApplicationLogger? logger = null)
    {
        var projectRepository = new InMemoryProjectRepository();
        var project = new TrackdubProject(
            Guid.NewGuid(),
            "Guard Test",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        projectRepository.Seed(project);

        var mediaRepository = new FakeMediaAssetRepository();
        var speakerRepository = new FakeSpeakerRepository();
        var artifactStore = new FakeArtifactStore();
        var transcriptRepository = new FakeTranscriptRepository();
        var translationRepository = new FakeTranslationRepository();
        var voiceAssignmentRepository = new FakeVoiceAssignmentRepository();
        var ttsTakeRepository = new FakeTtsTakeRepository();
        var stageRunStore = new FakeProjectStageRunStore();
        var translationLanguageRouter = new FakeTranslationLanguageRouter();
        var voiceCatalog = new FakeVoiceCatalog();
        var fileFingerprintService = new FakeFileFingerprintService();
        var artifactWriter = new TranscriptArtifactWriter(
            artifactStore,
            fileFingerprintService,
            mediaRepository);
        var projectMediaIngestService = new ProjectMediaIngestService(
            projectRepository,
            mediaRepository,
            artifactStore,
            new FakeMediaProbe(),
            new NoOpAudioExtractionService(),
            new NoOpWaveformSummaryGenerator(),
            fileFingerprintService,
            new FakeFileSystemProbe { TreatAllFilesAsExisting = true });
        var segmentEditingService = new SegmentEditingService(
            transcriptRepository,
            ttsTakeRepository,
            artifactWriter);
        var voiceAssignmentService = new VoiceAssignmentService(
            voiceAssignmentRepository,
            ttsTakeRepository,
            voiceCatalog);
        var durationAnalysisService = new DurationAnalysisService();
        var startTtsStageHandler = new StartTtsStageHandler(
            new FakeTtsEngine(),
            voiceCatalog,
            artifactStore,
            fileFingerprintService,
            mediaRepository,
            ttsTakeRepository,
            stageRunStore,
            durationAnalysisService,
            new FakeAudioTimeStretchService(),
            TtsTimingOptions.Default,
            new FakeTtsAudioPostProcessor());
        var ttsOrchestrationService = new TtsOrchestrationService(
            startTtsStageHandler,
            voiceAssignmentRepository,
            ttsTakeRepository,
            new FakeTtsEngine(),
            voiceCatalog,
            artifactStore,
            fileFingerprintService,
            mediaRepository,
            new FakeReferenceClipTrimmer(),
            durationAnalysisService,
            new FakeAudioTimeStretchService(),
            TtsTimingOptions.Default,
            new NoOpAudioClipExtractor(),
            new FakeReferenceClipAnalyzer());
        var diarizationStageHandler = new DiarizationStageHandler(
            new FakeDiarizationEngine(),
            new WritingModelDownloader(),
            modelCacheRoot: Path.Combine(Path.GetTempPath(), "trackdub-tests", Guid.NewGuid().ToString("N")),
            expectedSha256: SortFormerTestFixtures.ExpectedSha256);
        var referenceClipService = new SpeakerReferenceClipService(
            artifactStore,
            new NoOpAudioClipExtractor(),
            fileFingerprintService,
            mediaRepository,
            voiceAssignmentRepository,
            ttsTakeRepository,
            new FakeReferenceClipAnalyzer(),
            new FakeReferenceClipTrimmer());
        var speakerAssignmentService = new SpeakerAssignmentService(
            speakerRepository,
            transcriptRepository,
            segmentEditingService,
            artifactStore,
            stageRunStore,
            new FakeDiarizationEngine(),
            referenceClipService,
            artifactWriter,
            diarizationStageHandler);
        var degradationWriter = new PipelineDegradationWriter(
            artifactStore,
            fileFingerprintService,
            mediaRepository);
        var translationOrchestrationService = new TranslationOrchestrationService(
            translationRepository,
            new GlossaryService(new FakeGlossaryRepository()),
            new GlossaryTermMatcher(),
            translationLanguageRouter,
            new FakeTranslationEngine(),
            ttsTakeRepository,
            stageRunStore,
            artifactStore,
            artifactWriter);
        var stateService = new TranscriptProjectStateService(
            projectMediaIngestService,
            transcriptRepository,
            translationRepository,
            stageRunStore,
            speakerRepository,
            voiceAssignmentRepository,
            ttsTakeRepository,
            translationLanguageRouter,
            voiceCatalog,
            artifactStore,
            ttsOrchestrationService,
            voiceAssignmentService);
        var asrStageHandler = new AsrStageHandler(
            new NoOpAudioTranscriptionEngine(),
            stageRunStore);
        var transcriptGenerationService = new TranscriptGenerationService(
            transcriptRepository,
            artifactStore,
            asrStageHandler,
            artifactWriter,
            new VadGenerationStage(new VadStageHandler(new FakeSpeechRegionDetector(), stageRunStore), artifactWriter, artifactStore),
            new FakeEnhancementStage(),
            new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore),
            new AsrGenerationStage(asrStageHandler, artifactStore, stageRunStore, degradationWriter),
            new TextRefinementGenerationStage(
                new TextRefinementStageHandler(new FakeTextRefinementEngine(), stageRunStore),
                stageRunStore),
            new SpeakerAssignmentAndPersistenceStage(speakerAssignmentService, transcriptRepository, artifactWriter, artifactStore, stageRunStore),
            new FakePipelinePreFlightChecker(),
            stageRunStore,
            mediaRepository,
            speakerRepository);
        var previewMixWorkflow = new PreviewMixWorkflow(
            new MixPlanBuilder(),
            new MixPlanStore(artifactStore),
            new FakeMixRenderer(),
            artifactStore,
            fileFingerprintService,
            mediaRepository,
            stageRunStore);
        var exportWorkflow = new ExportWorkflow(
            stateService,
            new ExportStageHandler(
                new MixPlanBuilder(),
                new MixPlanStore(artifactStore),
                new FakeMixRenderer(),
                artifactStore,
                fileFingerprintService,
                mediaRepository,
                stageRunStore,
                new FakeLoudnessNormalizer(),
                new FakeExportRenderer(),
                new FakeMediaProbe(),
                new SubtitleExportService(),
                new FakeVideoRecomposer()));

        return new TranscriptWorkspace(
            new ProjectWorkflow(projectMediaIngestService, stateService, transcriptGenerationService),
            new DiarizationModelWorkflow(diarizationStageHandler),
            new TranscriptWorkflow(stateService, segmentEditingService, transcriptGenerationService),
            new TranslationWorkflow(stateService, translationOrchestrationService),
            new SpeakerWorkflow(stateService, speakerAssignmentService),
            new VoiceWorkflow(stateService, voiceAssignmentService, ttsOrchestrationService),
            new TtsWorkflow(stateService, ttsOrchestrationService),
            new TtsDubPreviewWorkflow(
                stateService,
                new TtsDubPreviewCoordinator(new FakeAudioPreviewTransport(), artifactStore)),
            previewMixWorkflow,
            exportWorkflow,
            new EditingHistoryWorkflow(
                stateService,
                speakerAssignmentService,
                voiceAssignmentService,
                segmentEditingService,
                translationOrchestrationService),
            importModelProvisioner: null,
            logger);
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

    private static async Task AssertGuardedOperationRejectedAsync(
        Func<TranscriptWorkspace, Task> operation)
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation(workspace));

        blocking.Release();
        Assert.Equal(1, await first);
    }

    /// <summary>
    /// Verifies that a light-write operation is NOT blocked by the pipeline guard when a pipeline
    /// operation is in progress. The operation may throw for other reasons (missing project state,
    /// unknown entity, etc.) but must not throw the pipeline-guard rejection message.
    /// </summary>
    private static async Task AssertNotBlockedByPipelineGuardAsync(
        Func<TranscriptWorkspace, Task> operation)
    {
        using TranscriptWorkspace workspace = CreateWorkspace();
        var blocking = new BlockingOperation();

        Task<int> first = workspace.RunPipelineAsync(
            "BlockingOperation",
            blocking.RunAsync,
            TestContext.Current.CancellationToken);
        await blocking.WaitForStartAsync();

        try
        {
            await operation(workspace);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.StartsWith("A pipeline operation is already running", StringComparison.Ordinal))
        {
            // If the pipeline guard rejected the light-write op, the test fails.
            Assert.Fail($"Light-write operation was incorrectly blocked by the pipeline guard: {ex.Message}");
        }
        catch
        {
            // Any other exception (no project loaded, entity not found, etc.) is fine —
            // it means the guard was bypassed and the operation reached its service layer.
        }

        blocking.Release();
        Assert.Equal(1, await first);
    }

    /// <summary>
    /// Overload for use in inline tests that already hold a workspace and a blocking operation.
    /// </summary>
    private static async Task AssertNotBlockedByPipelineGuardAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (InvalidOperationException ex)
            when (ex.Message.StartsWith("A pipeline operation is already running", StringComparison.Ordinal))
        {
            Assert.Fail($"Light-write operation was incorrectly blocked by the pipeline guard: {ex.Message}");
        }
        catch
        {
            // Any other exception is fine — the guard was bypassed.
        }
    }

    private sealed class BlockingOperation
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForStartAsync() => started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public void Release() => released.TrySetResult();

        public async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    private sealed class CapturingLogger : IApplicationLogger
    {
        public List<string> Warnings { get; } = [];

        public void LogDebug(string message)
        {
        }

        public void LogInformation(string message)
        {
        }

        public void LogWarning(string message, Exception? exception = null) =>
            Warnings.Add(message);

        public void LogError(string message, Exception? exception = null)
        {
        }
    }

    private sealed class InMemoryProjectRepository : IProjectRepository
    {
        private TrackdubProject? project;

        public void Seed(TrackdubProject seedProject) => project = seedProject;

        public Task InitializeAsync(TrackdubProject project, CancellationToken cancellationToken)
        {
            this.project = project;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(TrackdubProject project, CancellationToken cancellationToken)
        {
            this.project = project;
            return Task.CompletedTask;
        }

        public Task<TrackdubProject?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(project);
    }

    private sealed class NoOpAudioExtractionService : IAudioExtractionService
    {
        public Task<AudioExtractionResult> ExtractNormalizedAudioAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken,
            int? maxEncoderThreads = null) =>
            Task.FromResult(new AudioExtractionResult(destinationPath, 1d, 48000, 2, 48000));

        public Task<AudioExtractionResult> ExtractStemSeparationAudioAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AudioExtractionResult(destinationPath, 1d, 48000, 2, 48000));
    }

    private sealed class NoOpWaveformSummaryGenerator : IWaveformSummaryGenerator
    {
        public Task<WaveformSummary> GenerateAsync(string audioPath, CancellationToken cancellationToken) =>
            Task.FromResult(new WaveformSummary(1, 48000, 2, 1d, [0f]));
    }

    private sealed class NoOpAudioTranscriptionEngine : IAudioTranscriptionEngine
    {
        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>([]);
    }

    private sealed class NoOpAudioClipExtractor : IAudioClipExtractor
    {
        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            double startSeconds,
            double endSeconds,
            string destinationPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AudioClipExtractionResult(destinationPath, endSeconds - startSeconds, 48000, 2));

        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            IReadOnlyList<AudioClipRange> ranges,
            string destinationPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AudioClipExtractionResult(
                destinationPath,
                ranges.Sum(static range => range.EndSeconds - range.StartSeconds),
                48000,
                2));
    }

    private sealed class FakeEnhancementStage : Trackdub.Application.Transcripts.Pipeline.ITranscriptGenerationStage
    {
        public string StageName => Trackdub.Domain.StageRuns.StageNames.SpeechEnhancement;

        public Task<Trackdub.Application.Transcripts.Pipeline.TranscriptGenerationContext> ExecuteAsync(
            Trackdub.Application.Transcripts.Pipeline.TranscriptGenerationContext context,
            CancellationToken cancellationToken,
            IProgress<Trackdub.Contracts.Pipeline.PipelineProgressEvent>? progress = null) =>
            Task.FromResult(context);
    }
}
