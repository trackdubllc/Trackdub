using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Trackdub.Application.Dubbing;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;
using Trackdub.Sdk.Composition;
using Trackdub.TestDoubles;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Fake-backed execution coverage for <see cref="TrackdubDubbingEngine.ExecuteAsync"/>.
/// Existing SDK tests cover argument parsing and validation only; these exercise the engine
/// end-to-end so that pre-flight, stage-filter resolution, and result aggregation are also
/// covered. The pipeline still uses the real composition root, but we deliberately request
/// stages that operate against project-local artifacts only (Translation, Export) so that
/// the engine's media-existence guard and stage routing are observable without needing real
/// inference models on disk.
/// </summary>
public sealed class RunStageExecutionTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Theory]
    [InlineData(StageNames.Translation)]
    [InlineData(StageNames.Tts)]
    public async Task ExecuteAsync_TargetDependentStageWithoutTarget_FailsBeforeAnySideEffect(string stageName)
    {
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Join(tempDir, "not-created.trackdub");
        string missingMediaPath = Path.Join(tempDir, "missing-media.mp4");
        var sessionFactory = new ThrowingSessionFactory();
        var engine = new DubbingPipelineEngine(sessionFactory);

        DubbingRunResult result = await engine.ExecuteAsync(new DubbingSessionOptions
        {
            SourceMediaPath = missingMediaPath,
            ProjectOutputDirectory = projectDir,
            TargetLanguageCode = string.Empty,
            StageFilter = [stageName],
        });

        Assert.Equal(DubbingRunStatus.Failed, result.OverallStatus);
        Assert.Contains(
            result.PreFlightFailures!,
            failure => failure.Contains("Target language", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, sessionFactory.CreateSessionCalls);
        Assert.False(Directory.Exists(projectDir));
    }

    [Theory]
    [InlineData(StageNames.Separation)]
    [InlineData(StageNames.Vad)]
    [InlineData(StageNames.Diarization)]
    [InlineData(StageNames.Asr)]
    [InlineData(StageNames.Export)]
    [InlineData(StageNames.LipSync)]
    [InlineData(StageNames.LipSynthesis)]
    public async Task ExecuteAsync_TargetIndependentStageWithoutTarget_ReachesSessionCreation(string stageName)
    {
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Join(tempDir, "created.trackdub");
        string mediaPath = Path.Join(tempDir, "media.mp4");
        await File.WriteAllBytesAsync(mediaPath, [0x00]);
        var sessionFactory = new ThrowingSessionFactory();
        var engine = new DubbingPipelineEngine(sessionFactory);

        DubbingRunResult result = await engine.ExecuteAsync(new DubbingSessionOptions
        {
            SourceMediaPath = mediaPath,
            ProjectOutputDirectory = projectDir,
            TargetLanguageCode = string.Empty,
            StageFilter = [stageName],
        });

        Assert.Equal(DubbingRunStatus.Failed, result.OverallStatus);
        Assert.DoesNotContain(
            result.PreFlightFailures!,
            failure => failure.Contains("Target language", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, sessionFactory.CreateSessionCalls);
    }

    [Fact]
    public async Task ExecuteAsync_PreFlightFailure_PersistsExecutionSnapshot()
    {
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Combine(tempDir, "sample.trackdub");
        Directory.CreateDirectory(projectDir);
        string mediaPath = Path.Combine(tempDir, "video.mp4");
        await File.WriteAllBytesAsync(mediaPath, [0x00, 0x00, 0x00, 0x20]);

        var preFlightChecker = new FakePipelinePreFlightChecker();
        preFlightChecker.BlockStage(
            StageNames.Translation,
            new RequiredModelNotAvailableException("test/translation", "/missing/translation.onnx"));

        using TrackdubSessionFactory factory = CreateFactory(services =>
        {
            services.RemoveAll<IPipelineReadinessService>();
            services.Replace(ServiceDescriptor.Singleton<IPipelinePreFlightChecker>(preFlightChecker));
        });

        await using (TrackdubSession session = factory.CreateSession(projectDir))
        {
            await session.Workspace.CreateMediaSpineAsync(
                new CreateTranscriptProjectRequest("sample", mediaPath),
                CancellationToken.None);
        }

        var engine = new TrackdubDubbingEngine(factory);
        DubbingRunResult result = await engine.ExecuteAsync(new DubbingSessionOptions
        {
            SourceMediaPath = mediaPath,
            ProjectOutputDirectory = projectDir,
            TargetLanguageCode = "es",
            StageFilter = [StageNames.Translation, StageNames.Export],
        });

        Assert.Equal(DubbingRunStatus.PreFlightFailed, result.OverallStatus);
        Assert.NotNull(result.PreFlightFailures);
        Assert.Contains(
            result.PreFlightFailures!,
            failure => failure.Contains(StageNames.Translation, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result.ExecutionSnapshot);
        Assert.True(result.ExecutionSnapshot!.ContainsKey("TargetLanguageCode"));
    }

    [Fact]
    public async Task ExecuteAsync_StageFilterTargetingExportOnly_DoesNotRequireSourceMediaToExist()
    {
        // Stages that don't consume source media (translation, tts, export) must
        // not be gated by the source-media File.Exists check. This test asserts that the
        // engine no longer rejects an existing-project run whose source media has moved.
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Combine(tempDir, "project.trackdub");
        Directory.CreateDirectory(projectDir);

        var options = new DubbingSessionOptions
        {
            SourceMediaPath = Path.Combine(tempDir, "missing-source-media.mp4"),
            ProjectOutputDirectory = projectDir,
            TargetLanguageCode = "es",
            StageFilter = [StageNames.Export],
        };

        using var factory = CreateFactory();
        var engine = new TrackdubDubbingEngine(factory);

        DubbingRunResult result = await engine.ExecuteAsync(options);

        // The run will not Succeed (no upstream artifacts, no project DB), but it must NOT
        // fail with the "Media file not found" pre-flight diagnostic — that's the regression.
        if (result.PreFlightFailures is { Count: > 0 } failures)
        {
            Assert.DoesNotContain(failures, f => f.Contains("Media file not found", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task ProjectContextResolver_ReadsStoredSourceMediaPathFromDatabase()
    {
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Combine(tempDir, "sample.trackdub");
        Directory.CreateDirectory(projectDir);
        string mediaPath = Path.Combine(tempDir, "video.mp4");
        await File.WriteAllBytesAsync(mediaPath, [0x00, 0x00, 0x00, 0x20]);

        using TrackdubSessionFactory factory = CreateFactory();
        await using (TrackdubSession session = factory.CreateSession(projectDir))
        {
            await session.Workspace.CreateMediaSpineAsync(
                new CreateTranscriptProjectRequest("sample", mediaPath),
                CancellationToken.None);
        }

        TrackdubProjectContext? context = await TrackdubProjectContextResolver.TryOpenAsync(
            factory,
            projectDir,
            CancellationToken.None);

        Assert.NotNull(context);
        Assert.Equal(mediaPath, context!.SourceMediaPath);
    }

    [Fact]
    public async Task ExecuteAsync_StageRequiringSourceMedia_ResolvesStoredPathBeforeMediaValidation()
    {
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Combine(tempDir, "sample.trackdub");
        Directory.CreateDirectory(projectDir);
        string mediaPath = Path.Combine(tempDir, "video.mp4");
        await File.WriteAllBytesAsync(mediaPath, [0x00, 0x00, 0x00, 0x20]);

        using TrackdubSessionFactory factory = CreateFactory();
        await using (TrackdubSession session = factory.CreateSession(projectDir))
        {
            await session.Workspace.CreateMediaSpineAsync(
                new CreateTranscriptProjectRequest("sample", mediaPath),
                CancellationToken.None);
        }

        File.Delete(mediaPath);

        var engine = new TrackdubDubbingEngine(factory);
        DubbingRunResult result = await engine.ExecuteAsync(new DubbingSessionOptions
        {
            SourceMediaPath = Path.Combine(projectDir, "source-media"),
            ProjectOutputDirectory = projectDir,
            TargetLanguageCode = "es",
            StageFilter = [StageNames.Vad],
        });

        Assert.NotNull(result.PreFlightFailures);
        Assert.Contains(
            result.PreFlightFailures!,
            failure => failure.Contains("video.mp4", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.PreFlightFailures!,
            failure => failure.Contains("source-media", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_DeclinedOptionalSeparationModel_SkipsStageInsteadOfPreFlightFailure()
    {
        // Regression: a user-declined optional model used to re-block on the
        // post-provisioning readiness pass and surface as PreFlightFailed.
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Combine(tempDir, "sample.trackdub");
        Directory.CreateDirectory(projectDir);
        string mediaPath = Path.Combine(tempDir, "video.mp4");
        await File.WriteAllBytesAsync(mediaPath, [0x00, 0x00, 0x00, 0x20]);

        using TrackdubSessionFactory factory = CreateFactory(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IPipelineReadinessService>(
                new FakePipelineReadinessService(ReadinessState.DownloadRequired)));
            services.Replace(ServiceDescriptor.Singleton<IPipelineModelSetupInteraction>(
                new SkipOptionalStageSetupInteraction()));
            services.Replace(ServiceDescriptor.Singleton<IRuntimeModelBootstrapService>(
                new MissingRuntimeModelBootstrapService()));
        });

        await using (TrackdubSession session = factory.CreateSession(projectDir))
        {
            await session.Workspace.CreateMediaSpineAsync(
                new CreateTranscriptProjectRequest("sample", mediaPath),
                CancellationToken.None);
        }

        var engine = new TrackdubDubbingEngine(factory);
        DubbingRunResult result = await engine.ExecuteAsync(new DubbingSessionOptions
        {
            SourceMediaPath = mediaPath,
            ProjectOutputDirectory = projectDir,
            TargetLanguageCode = "es",
            EnableStemSeparation = true,
            StageFilter = [StageNames.Separation],
        });

        Assert.NotEqual(DubbingRunStatus.PreFlightFailed, result.OverallStatus);
        StageOutcome outcome = Assert.Single(result.StageOutcomes);
        Assert.Equal(StageNames.Separation, outcome.StageName);
        Assert.Equal(StageStatus.Skipped, outcome.Status);
        Assert.Equal(StageSkipReasonCodes.OptionalModelDeclined, outcome.ReasonCode);
    }

    [Fact]
    public async Task ExecuteAsync_DeclinedOptionalSpeechEnhancement_RunsFallbackInsteadOfSkip()
    {
        // Speech enhancement has an in-place FFmpeg/AFX fallback. A declined model
        // should still execute audio preparation, not produce OPTIONAL_MODEL_DECLINED.
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Combine(tempDir, "sample.trackdub");
        Directory.CreateDirectory(projectDir);
        string mediaPath = Path.Combine(tempDir, "video.mp4");
        await File.WriteAllBytesAsync(mediaPath, [0x00, 0x00, 0x00, 0x20]);

        using TrackdubSessionFactory factory = CreateFactory(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IPipelineReadinessService>(
                new FakePipelineReadinessService(ReadinessState.DownloadRequired)));
            services.Replace(ServiceDescriptor.Singleton<IPipelineModelSetupInteraction>(
                new SkipOptionalStageSetupInteraction()));
            services.Replace(ServiceDescriptor.Singleton<IRuntimeModelBootstrapService>(
                new MissingRuntimeModelBootstrapService()));
        });

        await using (TrackdubSession session = factory.CreateSession(projectDir))
        {
            await session.Workspace.CreateMediaSpineAsync(
                new CreateTranscriptProjectRequest("sample", mediaPath),
                CancellationToken.None);
        }

        var engine = new TrackdubDubbingEngine(factory);
        DubbingRunResult result = await engine.ExecuteAsync(new DubbingSessionOptions
        {
            SourceMediaPath = mediaPath,
            ProjectOutputDirectory = projectDir,
            TargetLanguageCode = "es",
            StageFilter = [StageNames.AudioPreparation],
        });

        Assert.NotEqual(DubbingRunStatus.PreFlightFailed, result.OverallStatus);
        StageOutcome outcome = Assert.Single(result.StageOutcomes);
        Assert.Equal(StageNames.AudioPreparation, outcome.StageName);
        // Stage executes through FFmpeg fallback; does not skip with OPTIONAL_MODEL_DECLINED.
        Assert.NotEqual(StageStatus.Skipped, outcome.Status);
        Assert.NotEqual(StageSkipReasonCodes.OptionalModelDeclined, outcome.ReasonCode);
    }

    private static TrackdubSessionFactory CreateFactory(Action<IServiceCollection>? configureServices = null)
    {
        var options = new TrackdubOptions
        {
            ServiceConfigurator = services =>
            {
                // Fake probe + fingerprint so CreateMediaSpineAsync does not depend on
                // Windows CI temp-file durability between File.Exists and SHA256 open.
                services.Replace(ServiceDescriptor.Singleton<IMediaProbe, FakeMediaProbe>());
                services.Replace(ServiceDescriptor.Singleton<IFileFingerprintService>(
                    new FakeFileFingerprintService(
                        new FileFingerprint("sdk-test-media-hash", 4, DateTimeOffset.UnixEpoch))));
                configureServices?.Invoke(services);
            },
        };
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub(options);
        var provider = services.BuildServiceProvider();
        return new TrackdubSessionFactory(provider);
    }

    private string CreateTempProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private sealed class FakePipelineReadinessService(ReadinessState status) : IPipelineReadinessService
    {
        public Task<PipelineReadinessReport> EvaluateAsync(
            IReadOnlyList<RuntimeStage> enabledStages,
            RuntimeModelSelections selections,
            TranscriptProjectState? state,
            CancellationToken cancellationToken = default,
            string? sourceLanguageCode = null,
            string? targetLanguageCode = null,
            bool validateRuntime = true) =>
            Task.FromResult(new PipelineReadinessReport(enabledStages
                .Select(stage => new StageReadiness(
                    StageName: StageNameFor(stage),
                    Status: status,
                    Detail: "fake",
                    ModelId: "fake/model",
                    ModelAlias: null,
                    ResolveAction: "download"))
                .ToArray()));

        public void InvalidateCache(IReadOnlyList<RuntimeStage>? stages = null)
        {
        }

        private static string StageNameFor(RuntimeStage stage) => stage switch
        {
            RuntimeStage.Vad => StageNames.Vad,
            RuntimeStage.Asr => StageNames.Asr,
            RuntimeStage.Translation => StageNames.Translation,
            RuntimeStage.Tts => StageNames.Tts,
            RuntimeStage.Diarization => StageNames.Diarization,
            RuntimeStage.Separation => StageNames.Separation,
            RuntimeStage.SpeechEnhancement => StageNames.SpeechEnhancement,
            RuntimeStage.LipSync => StageNames.LipSync,
            RuntimeStage.TextRefinement => StageNames.TextRefinementAsr,
            RuntimeStage.OverlapRescue => StageNames.OverlapRescue,
            RuntimeStage.LipSynthesis => StageNames.LipSynthesis,
            _ => stage.ToString().ToLowerInvariant(),
        };
    }

    private sealed class SkipOptionalStageSetupInteraction : IPipelineModelSetupInteraction
    {
        public RuntimeModelSetupCallbacks CreateCallbacks(
            IProgress<PipelineProgressEvent>? progress,
            CancellationToken cancellationToken) =>
            new(
                ResolveDecisionAsync: _ => Task.FromResult(RuntimeModelSetupDecision.SkipOptionalStage),
                PickImportFileAsync: () => Task.FromResult<string?>(null),
                CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                RunOperationAsync: (operation, _) => operation(cancellationToken));
    }

    private sealed class MissingRuntimeModelBootstrapService : IRuntimeModelBootstrapService
    {
        public Task<RequiredRuntimeModelStatus?> GetRequiredModelStatusAsync(
            RuntimeModelRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RequiredRuntimeModelStatus?>(new RequiredRuntimeModelStatus(
                Stage: request.Stage,
                StageDisplayName: request.Stage.ToString(),
                ModelId: "fake/model",
                ModelAlias: request.PreferredModelAlias,
                Variant: null,
                ExpectedFileName: "model.onnx",
                ModelPath: "missing/model.onnx",
                SourceUrl: "https://example.invalid/model.onnx",
                License: "test",
                IsAvailable: false,
                CanAutoDownload: true,
                CanImportSingleFile: true,
                RequiresAttribution: false,
                RequiresUserConsent: false,
                HelpText: "fake"));

        public Task<RequiredRuntimeModelStatus> DownloadRequiredModelAsync(
            RuntimeModelRequest request,
            IProgress<ModelDownloadProgress>? downloadProgress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Test must not download.");

        public Task<RequiredRuntimeModelStatus> ImportRequiredModelAsync(
            RuntimeModelRequest request,
            string sourceModelPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Test must not import.");

        public Task<RequiredRuntimeModelStatus?> GetManifestCompanionModelStatusAsync(
            string manifestAlias,
            RuntimeStage owningStage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RequiredRuntimeModelStatus?>(null);

        public Task<RequiredRuntimeModelStatus> DownloadManifestCompanionModelAsync(
            string manifestAlias,
            RuntimeStage owningStage,
            IProgress<ModelDownloadProgress>? downloadProgress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Test must not download.");
    }

    private sealed class ThrowingSessionFactory : IDubbingSessionFactory
    {
        public int CreateSessionCalls { get; private set; }

        public IDubbingSession CreateSession(string projectRootPath, StudioSettings? settings = null)
        {
            CreateSessionCalls++;
            throw new InvalidOperationException("Session creation sentinel.");
        }

        public void Dispose()
        {
        }
    }
}
