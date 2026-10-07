using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class StartTtsStageHandlerLifecycleTests
{
    [Fact]
    public async Task HandleAsync_WhenVoiceMissing_FailsStageRunBeforeSynthesis()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        using var handler = CreateHandler(stageRunStore);
        StartTtsStageRequest request = CreateRequest(voiceId: "missing-voicepack");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(request, TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Failed, run.Status);
        Assert.Contains("missing-voicepack", run.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WhenCanceledDuringSynthesis_RecordsCanceledStatus()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        using var cancellation = new CancellationTokenSource();
        using var handler = CreateHandler(stageRunStore, new DelayingTtsEngine());
        StartTtsStageRequest request = CreateRequest();

        Task<StartTtsStageResult> runTask = handler.HandleAsync(request, cancellation.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Canceled, run.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenSynthesisFails_RecordsFailedStatus()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        using var handler = CreateHandler(stageRunStore, new ThrowingTtsEngine());
        StartTtsStageRequest request = CreateRequest();
        var progress = new CollectingProgress();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(request, TestContext.Current.CancellationToken, progress));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Failed, run.Status);
        Assert.DoesNotContain(progress.Events, e => e.OutputKind is not null);
    }

    [Fact]
    public async Task HandleAsync_WithValidInput_CompletesStageRun()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        using var handler = CreateHandler(stageRunStore);
        StartTtsStageRequest request = CreateRequest();
        var progress = new CollectingProgress();

        StartTtsStageResult result = await handler.HandleAsync(
            request, TestContext.Current.CancellationToken, progress);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        TtsTake take = Assert.Single(result.Takes);

        PipelineProgressEvent output = Assert.Single(progress.Events, e => e.OutputKind is not null);
        Assert.Equal(PipelineOutputKind.PlayableAudioPersisted, output.OutputKind);
        Assert.Equal(request.TranslatedSegments[0].SegmentIndex, output.ItemIndex);
        Assert.Equal(request.TranslatedSegments[0].Id, output.SegmentId);
        Assert.NotNull(output.ArtifactId);
        Assert.NotEqual(Guid.Empty, output.ArtifactId!.Value);
        Assert.Equal(take.ArtifactId, output.ArtifactId);
    }

    [Fact]
    public async Task HandleAsync_NonEnglishSpanishStockVoice_UsesQwenCustomVoice()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var ttsEngine = new FakeTtsEngine();
        using var handler = CreateHandler(stageRunStore, ttsEngine);
        StartTtsStageRequest request = CreateRequest(targetLanguage: "fr");

        await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(
            Qwen3TtsDefaults.CustomVoice06Alias,
            ttsEngine.LastOptions?.PreferredModelAlias);
        Assert.Equal("qwen3:ryan", ttsEngine.LastVoicepack?.VoiceId);
        Assert.Null(ttsEngine.LastRequest?.VoiceCloneReference);
    }

    [Fact]
    public async Task HandleAsync_WhenCompletionFailsAfterTakesProduced_RecordsPartiallyCompletedStatus()
    {
        var stageRunStore = new ThrowOnCompletedUpdateStageRunStore();
        var ttsTakeRepository = new FakeTtsTakeRepository();
        using var handler = CreateHandler(stageRunStore, ttsTakeRepository: ttsTakeRepository);
        StartTtsStageRequest request = CreateRequest();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(request, TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.PartiallyCompleted, run.Status);
        Assert.Contains("TTS generated 1 take(s) before failing", run.FailureReason, StringComparison.Ordinal);
        Assert.Contains(
            "Terminal completion persistence failed",
            run.FailureReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(ttsTakeRepository.All);
    }

    [Fact]
    public async Task HandleAsync_WhenLaterSegmentPersistenceFails_RecordsPartiallyCompletedStatus()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var ttsTakeRepository = new ThrowOnSecondTakeSaveRepository();
        using var handler = CreateHandler(stageRunStore, ttsTakeRepository: ttsTakeRepository);
        StartTtsStageRequest request = CreateMultiSegmentRequest(segmentCount: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(request, TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.PartiallyCompleted, run.Status);
        Assert.Contains("TTS generated 1 take(s) before failing", run.FailureReason, StringComparison.Ordinal);
        Assert.Contains(
            "Second take persistence failed",
            run.FailureReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(ttsTakeRepository.Inner.All);
    }

    [Fact]
    public async Task HandleAsync_ChineseTarget_UsesNativeMandarinQwen3Preset()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var ttsEngine = new FakeTtsEngine();
        using var handler = CreateHandler(stageRunStore, ttsEngine);
        StartTtsStageRequest request = CreateRequest(targetLanguage: "zh");

        StartTtsStageResult result = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Equal(Qwen3TtsDefaults.CustomVoice06Alias, ttsEngine.LastOptions?.PreferredModelAlias);
        Assert.Equal("qwen3:vivian", ttsEngine.LastVoicepack?.VoiceId);
    }

    [Fact]
    public async Task HandleAsync_AssignedQwen3Preset_OnSpanishTarget_RoutesToCustomVoice()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var ttsEngine = new FakeTtsEngine();
        var catalog = new FakeVoiceCatalog(
        [
            new("af_heart", "mul", "female", "Heart"),
            new("qwen3:serena", "mul", "female", "Serena (Chinese)"),
        ]);
        using var handler = CreateHandler(stageRunStore, ttsEngine, voiceCatalog: catalog);
        StartTtsStageRequest request = CreateRequest(voiceId: "qwen3:serena", targetLanguage: "es");

        StartTtsStageResult result = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Equal(Qwen3TtsDefaults.CustomVoice06Alias, ttsEngine.LastOptions?.PreferredModelAlias);
        Assert.True(ttsEngine.LastOptions?.RequirePreferredModelAlias);
        Assert.Equal("qwen3:serena", ttsEngine.LastVoicepack?.VoiceId);
    }

    [Fact]
    public async Task HandleAsync_CustomVoiceAlias_WithKokoroAssignment_UsesDefaultPresetInsteadOfFailing()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var ttsEngine = new FakeTtsEngine();
        using var handler = CreateHandler(stageRunStore, ttsEngine);
        StartTtsStageRequest request = CreateRequest(targetLanguage: "es") with
        {
            PreferredModelAlias = Qwen3TtsDefaults.CustomVoice06Alias,
        };

        StartTtsStageResult result = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Equal("qwen3:ryan", ttsEngine.LastVoicepack?.VoiceId);
    }

    [Fact]
    public async Task HandleAsync_CloneOnlyAliasWithoutCloning_WarnsAboutStockSubstitution()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var ttsEngine = new FakeTtsEngine();
        var logger = new WarningCapturingLogger();
        using var handler = CreateHandler(stageRunStore, ttsEngine, logger: logger);
        StartTtsStageRequest request = CreateRequest(targetLanguage: "es") with
        {
            PreferredModelAlias = VoiceCloningDefaults.ChatterboxMultilingualAlias,
        };

        StartTtsStageResult result = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        string warning = Assert.Single(logger.Warnings);
        Assert.Contains(VoiceCloningDefaults.ChatterboxMultilingualAlias, warning, StringComparison.Ordinal);
        Assert.Contains("needs voice cloning", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_WhenCanceledWhileRecordingSubstitution_RecordsCanceledStatus()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var artifactStore = new CancelingJsonArtifactStore();
        var degradationWriter = new PipelineDegradationWriter(
            artifactStore,
            new FakeFileFingerprintService(new FileFingerprint("tts-hash", 42, DateTimeOffset.UtcNow)),
            new FakeMediaAssetRepository());
        using var handler = new StartTtsStageHandler(
            new FakeTtsEngine(),
            new FakeVoiceCatalog(),
            artifactStore,
            new FakeFileFingerprintService(new FileFingerprint("tts-hash", 42, DateTimeOffset.UtcNow)),
            new FakeMediaAssetRepository(),
            new FakeTtsTakeRepository(),
            stageRunStore,
            degradationWriter: degradationWriter);
        StartTtsStageRequest request = CreateRequest(targetLanguage: "es") with
        {
            PreferredModelAlias = VoiceCloningDefaults.ChatterboxMultilingualAlias,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.HandleAsync(request, TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Canceled, run.Status);
    }

    [Fact]
    public async Task HandleAsync_StockRunWithoutRequestedAlias_DoesNotWarn()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var logger = new WarningCapturingLogger();
        using var handler = CreateHandler(stageRunStore, logger: logger);
        StartTtsStageRequest request = CreateRequest(targetLanguage: "es");

        await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Empty(logger.Warnings);
    }

    private static StartTtsStageHandler CreateHandler(
        IProjectStageRunStore stageRunStore,
        FakeTtsEngine? ttsEngine = null,
        ITtsTakeRepository? ttsTakeRepository = null,
        IVoiceCatalog? voiceCatalog = null,
        IApplicationLogger? logger = null)
    {
        return new StartTtsStageHandler(
            ttsEngine ?? new FakeTtsEngine(),
            voiceCatalog ?? new FakeVoiceCatalog(),
            new FakeArtifactStore(),
            new FakeFileFingerprintService(new FileFingerprint("tts-hash", 42, DateTimeOffset.UtcNow)),
            new FakeMediaAssetRepository(),
            ttsTakeRepository ?? new FakeTtsTakeRepository(),
            stageRunStore,
            logger: logger);
    }

    private sealed class CancelingJsonArtifactStore : IArtifactStore
    {
        private readonly FakeArtifactStore inner = new();

        public Task EnsureLayoutAsync(CancellationToken cancellationToken) => inner.EnsureLayoutAsync(cancellationToken);

        public ArtifactWriteHandle CreateWriteHandle(string relativePath) => inner.CreateWriteHandle(relativePath);

        public Task CommitAsync(ArtifactWriteHandle handle, CancellationToken cancellationToken) =>
            inner.CommitAsync(handle, cancellationToken);

        public Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken) =>
            throw new OperationCanceledException("Canceled while writing a degradation record.");

        public Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken) =>
            inner.ReadJsonAsync<T>(relativePath, cancellationToken);

        public string GetPath(string relativePath) => inner.GetPath(relativePath);

        public bool Exists(string relativePath) => inner.Exists(relativePath);
    }

    private sealed class WarningCapturingLogger : IApplicationLogger
    {
        public List<string> Warnings { get; } = [];

        public void LogDebug(string message) { }

        public void LogInformation(string message) { }

        public void LogWarning(string message, Exception? exception = null) => Warnings.Add(message);

        public void LogError(string message, Exception? exception = null) { }
    }

    [Fact]
    public async Task HandleAsync_NonEnglishSpanishTarget_WithoutReferenceAudio_UsesQwen3CustomVoiceAlias()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var ttsEngine = new FakeTtsEngine();
        using var handler = CreateHandler(stageRunStore, ttsEngine);
        StartTtsStageRequest request = CreateRequest(targetLanguage: "fr");

        StartTtsStageResult result = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.NotNull(ttsEngine.LastOptions);
        Assert.Equal(Qwen3TtsDefaults.ResolveCustomVoiceAlias(tier: null), ttsEngine.LastOptions!.PreferredModelAlias);
        Assert.NotEqual(VoiceCloningDefaults.CosyVoicePrimaryAlias, ttsEngine.LastOptions.PreferredModelAlias);
    }

    [Fact]
    public async Task HandleAsync_EnglishTarget_WithoutReferenceAudio_StaysOnStockKokoroAlias()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var ttsEngine = new FakeTtsEngine();
        using var handler = CreateHandler(stageRunStore, ttsEngine);
        StartTtsStageRequest request = CreateRequest(targetLanguage: "en");

        StartTtsStageResult result = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.NotNull(ttsEngine.LastOptions);
        Assert.Equal(StockTtsDefaults.KokoroPrimaryAlias, ttsEngine.LastOptions!.PreferredModelAlias);
    }

    private static StartTtsStageRequest CreateRequest(
        string voiceId = "af_heart",
        string targetLanguage = "es")
    {
        Guid projectId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
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
        TranscriptSegment transcriptSegment = TranscriptSegment.Create(
            Guid.NewGuid(),
            0,
            0.0d,
            1.0d,
            "Hello.",
            speakerId,
            "en");
        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            Guid.NewGuid(),
            0,
            0.0d,
            1.0d,
            "Hola.");
        VoiceAssignment voiceAssignment = VoiceAssignment.Create(projectId, speakerId, voiceId);

        return new StartTtsStageRequest(
            projectId,
            mediaAsset,
            speakerId,
            targetLanguage,
            voiceAssignment,
            [transcriptSegment],
            [translatedSegment]);
    }

    private static StartTtsStageRequest CreateMultiSegmentRequest(int segmentCount, string voiceId = "af_heart")
    {
        Guid projectId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            segmentCount + 1.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        TranscriptSegment[] transcriptSegments = Enumerable.Range(0, segmentCount)
            .Select(index => TranscriptSegment.Create(
                Guid.NewGuid(),
                index,
                index,
                index + 1.0d,
                $"Line {index}.",
                speakerId,
                "en"))
            .ToArray();
        TranslatedSegment[] translatedSegments = transcriptSegments
            .Select(segment => TranslatedSegment.Create(
                Guid.NewGuid(),
                segment.SegmentIndex,
                segment.StartSeconds,
                segment.EndSeconds,
                $"ES {segment.SegmentIndex}"))
            .ToArray();
        VoiceAssignment voiceAssignment = VoiceAssignment.Create(projectId, speakerId, voiceId);

        return new StartTtsStageRequest(
            projectId,
            mediaAsset,
            speakerId,
            "es",
            voiceAssignment,
            transcriptSegments,
            translatedSegments);
    }

    private sealed class CollectingProgress : IProgress<PipelineProgressEvent>
    {
        public List<PipelineProgressEvent> Events { get; } = [];

        public void Report(PipelineProgressEvent value)
        {
            lock (Events)
            {
                Events.Add(value);
            }
        }
    }

    private sealed class DelayingTtsEngine : FakeTtsEngine
    {
        public override async Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            return await base.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ThrowingTtsEngine : FakeTtsEngine
    {
        public override Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("TTS synthesis failed.");
    }

    private sealed class ThrowOnCompletedUpdateStageRunStore : IProjectStageRunStore
    {
        private readonly FakeProjectStageRunStore inner = new();

        public IReadOnlyList<StageRunRecord> All => inner.All;

        public Task CreateAsync(StageRunRecord stageRun, CancellationToken cancellationToken) =>
            inner.CreateAsync(stageRun, cancellationToken);

        public Task<IReadOnlyList<StageRunRecord>> ListByProjectAsync(
            Guid projectId,
            CancellationToken cancellationToken) =>
            inner.ListByProjectAsync(projectId, cancellationToken);

        public Task UpdateAsync(StageRunRecord stageRun, CancellationToken cancellationToken)
        {
            if (stageRun.Status == StageRunStatus.Completed)
            {
                throw new InvalidOperationException("Terminal completion persistence failed.");
            }

            return inner.UpdateAsync(stageRun, cancellationToken);
        }
    }

    /// <summary>Throws on the second take save so the first segment can finish before persistence fails.</summary>
    private sealed class ThrowOnSecondTakeSaveRepository : ITtsTakeRepository
    {
        private readonly SemaphoreSlim saveGate = new(1, 1);
        private int saveCount;

        public FakeTtsTakeRepository Inner { get; } = new();

        public Task<TtsTake?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Inner.GetAsync(id, cancellationToken);

        public Task<TtsTake?> GetByFingerprintAsync(
            Guid projectId,
            string inputFingerprint,
            CancellationToken cancellationToken) =>
            Inner.GetByFingerprintAsync(projectId, inputFingerprint, cancellationToken);

        public Task<IReadOnlyList<TtsTake>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
            Inner.GetByProjectAsync(projectId, cancellationToken);

        public Task<IReadOnlyList<TtsTake>> GetBySegmentAsync(
            Guid translatedSegmentId,
            CancellationToken cancellationToken) =>
            Inner.GetBySegmentAsync(translatedSegmentId, cancellationToken);

        public Task<IReadOnlyList<TtsTake>> GetStaleBySpeakerAsync(
            Guid projectId,
            Guid voiceAssignmentId,
            CancellationToken cancellationToken) =>
            Inner.GetStaleBySpeakerAsync(projectId, voiceAssignmentId, cancellationToken);

        public Task MarkBySegmentIndicesStaleAsync(
            Guid projectId,
            IReadOnlySet<int> segmentIndices,
            CancellationToken cancellationToken) =>
            Inner.MarkBySegmentIndicesStaleAsync(projectId, segmentIndices, cancellationToken);

        public Task MarkByVoiceAssignmentStaleAsync(
            Guid projectId,
            Guid voiceAssignmentId,
            CancellationToken cancellationToken) =>
            Inner.MarkByVoiceAssignmentStaleAsync(projectId, voiceAssignmentId, cancellationToken);

        public async Task SaveAsync(TtsTake take, CancellationToken cancellationToken)
        {
            await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Interlocked.Increment(ref saveCount) >= 2)
                {
                    throw new InvalidOperationException("Second take persistence failed.");
                }

                await Inner.SaveAsync(take, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                saveGate.Release();
            }
        }
    }
}
