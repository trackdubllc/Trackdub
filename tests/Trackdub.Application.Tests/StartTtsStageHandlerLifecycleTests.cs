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

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(request, TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task HandleAsync_WithValidInput_CompletesStageRun()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        using var handler = CreateHandler(stageRunStore);
        StartTtsStageRequest request = CreateRequest();

        StartTtsStageResult result = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Single(result.Takes);
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

    private static StartTtsStageHandler CreateHandler(
        IProjectStageRunStore stageRunStore,
        FakeTtsEngine? ttsEngine = null,
        ITtsTakeRepository? ttsTakeRepository = null)
    {
        return new StartTtsStageHandler(
            ttsEngine ?? new FakeTtsEngine(),
            new FakeVoiceCatalog(),
            new FakeArtifactStore(),
            new FakeFileFingerprintService(new FileFingerprint("tts-hash", 42, DateTimeOffset.UtcNow)),
            new FakeMediaAssetRepository(),
            ttsTakeRepository ?? new FakeTtsTakeRepository(),
            stageRunStore);
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
