using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

/// <summary>
/// D2 handler-level tests: the VRAM-aware TTS concurrency cap actually binds the synthesis
/// fan-out. A tracking engine records the peak number of concurrent synthesis calls; the
/// handler must never exceed the effective concurrency resolved for the routed model.
/// </summary>
public sealed class StartTtsStageHandlerConcurrencyTests
{
    [Fact]
    public async Task HandleAsync_LargeModelOnSmallVram_NeverExceedsOneConcurrentSynthesis()
    {
        // Qwen3-1.7B (large class, 16384 MB budget) cannot fit twice in 6144 MB: cap = 1.
        var engine = new ConcurrencyTrackingEngine(TimeSpan.FromMilliseconds(50));
        var stageRunStore = new FakeProjectStageRunStore();
        using var handler = CreateHandler(engine, stageRunStore, new TtsExecutionOptions(8, 6144));
        StartTtsStageRequest request = CreateMultiSegmentRequest(
            segmentCount: 4, voiceId: "af_heart", preferredModelAlias: "qwen3-tts-1.7b-customvoice");

        StartTtsStageResult result = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, Assert.Single(stageRunStore.All).Status);
        Assert.Equal(4, engine.CompletedCount);
        Assert.True(engine.PeakConcurrency <= 1, $"Expected peak concurrency <= 1, got {engine.PeakConcurrency}.");
    }

    [Fact]
    public async Task HandleAsync_SmallModelWithUnknownVram_RunsConcurrently()
    {
        // Unknown VRAM -> configured cap applies unchanged (4); with 4+ segments and a
        // delayed engine, the peak must exceed 1 to prove the fan-out is not over-tightened.
        var engine = new ConcurrencyTrackingEngine(TimeSpan.FromMilliseconds(150));
        var stageRunStore = new FakeProjectStageRunStore();
        using var handler = CreateHandler(engine, stageRunStore, new TtsExecutionOptions(4, 0));
        StartTtsStageRequest request = CreateMultiSegmentRequest(segmentCount: 8, voiceId: "af_heart");

        StartTtsStageResult result = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, Assert.Single(stageRunStore.All).Status);
        Assert.Equal(8, engine.CompletedCount);
        Assert.True(engine.PeakConcurrency > 1, $"Expected concurrent synthesis (>1), got peak {engine.PeakConcurrency}.");
    }

    [Fact]
    public async Task HandleAsync_PartialFailureUnderCap_StillRecordsPartiallyCompleted()
    {
        // C8 coherence under the new cap: a failing engine under concurrency 1 still yields
        // a PartiallyCompleted stage run with the takes that succeeded before the failure.
        var engine = new FailAfterTrackingEngine(failAfter: 2, workDelay: TimeSpan.FromMilliseconds(20));
        var stageRunStore = new FakeProjectStageRunStore();
        using var handler = CreateHandler(engine, stageRunStore, new TtsExecutionOptions(8, 6144));
        StartTtsStageRequest request = CreateMultiSegmentRequest(
            segmentCount: 4, voiceId: "af_heart", preferredModelAlias: "qwen3-tts-1.7b-customvoice");

        // Synthesis failure propagates (matches HandleAsync_WhenLaterSegmentPersistenceFails...):
        // the assertion is that the failure path still records the takes made before failing.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(request, TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.PartiallyCompleted, run.Status);
        Assert.Contains("TTS generated 2 take(s) before failing", run.FailureReason, StringComparison.Ordinal);
        Assert.True(engine.PeakConcurrency <= 1, $"Expected peak <= 1 under large-model cap, got {engine.PeakConcurrency}.");
    }

    [Fact]
    public async Task HandleAsync_PlannedSmallAdapter_CapsByThatAdapterNotHostMax()
    {
        // Host max 24 GB would allow 8 medium-model workers; the plan places synthesis on a
        // 4 GB adapter, whose budget allows 1.
        var engine = new ConcurrencyTrackingEngine(TimeSpan.FromMilliseconds(50));
        var stageRunStore = new FakeProjectStageRunStore();
        var resolver = new FixedPlacementResolver(new TtsAcceleratorPlacement(AcceleratorRouted: true, DeviceVramMb: 4096));
        using var handler = CreateHandler(engine, stageRunStore, new TtsExecutionOptions(8, 24576), resolver);
        StartTtsStageRequest request = CreateMultiSegmentRequest(
            segmentCount: 4, voiceId: "af_heart", preferredModelAlias: "qwen3-tts-0.6b-customvoice");

        await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(4, engine.CompletedCount);
        Assert.Equal(1, resolver.CallCount);
        Assert.True(engine.PeakConcurrency <= 1, $"Expected peak concurrency <= 1, got {engine.PeakConcurrency}.");
    }

    [Fact]
    public async Task HandleAsync_PlacementResolverFails_FallsBackToHostBound()
    {
        var engine = new ConcurrencyTrackingEngine(TimeSpan.FromMilliseconds(50));
        var stageRunStore = new FakeProjectStageRunStore();
        var resolver = new FixedPlacementResolver(failure: new InvalidOperationException("planner down"));
        using var handler = CreateHandler(engine, stageRunStore, new TtsExecutionOptions(8, 6144), resolver);
        StartTtsStageRequest request = CreateMultiSegmentRequest(
            segmentCount: 4, voiceId: "af_heart", preferredModelAlias: "qwen3-tts-1.7b-customvoice");

        await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, Assert.Single(stageRunStore.All).Status);
        Assert.True(engine.PeakConcurrency <= 1, $"Expected host-bound peak <= 1, got {engine.PeakConcurrency}.");
    }

    private sealed class FixedPlacementResolver(
        TtsAcceleratorPlacement? placement = null,
        Exception? failure = null) : ITtsAcceleratorPlacementResolver
    {
        public int CallCount { get; private set; }

        public Task<TtsAcceleratorPlacement?> ResolvePlacementAsync(
            InferenceRequestOptions options,
            string? languageCode,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return failure is null ? Task.FromResult(placement) : Task.FromException<TtsAcceleratorPlacement?>(failure);
        }
    }

    private static StartTtsStageHandler CreateHandler(
        FakeTtsEngine engine,
        FakeProjectStageRunStore stageRunStore,
        TtsExecutionOptions executionOptions,
        ITtsAcceleratorPlacementResolver? placementResolver = null)
    {
        var artifactStore = new FakeArtifactStore();
        var mediaRepository = new FakeMediaAssetRepository();
        var takeRepository = new FakeTtsTakeRepository();
        return new StartTtsStageHandler(
            engine,
            new FakeVoiceCatalog(),
            artifactStore,
            new FakeFileFingerprintService(new FileFingerprint("tts-hash", 42, DateTimeOffset.UtcNow)),
            mediaRepository,
            takeRepository,
            stageRunStore,
            logger: null,
            commitBoundary: TestAtomicCommitBoundary.Create(null, takeRepository, artifactStore, mediaRepository),
            executionOptions: executionOptions,
            placementResolver: placementResolver);
    }

    private static StartTtsStageRequest CreateMultiSegmentRequest(
        int segmentCount,
        string voiceId,
        string? preferredModelAlias = null)
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
            translatedSegments,
            PreferredModelAlias: preferredModelAlias);
    }

    /// <summary>Engine that tracks the peak number of simultaneous synthesis calls.</summary>
    private sealed class ConcurrencyTrackingEngine(TimeSpan workDelay) : FakeTtsEngine
    {
        private int inFlight;
        private int peakConcurrency;
        private int completedCount;
        private readonly object gate = new();

        public int PeakConcurrency => peakConcurrency;

        public int CompletedCount => completedCount;

        public override async Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref inFlight);
            lock (gate)
            {
                if (current > peakConcurrency)
                {
                    peakConcurrency = current;
                }
            }

            try
            {
                await Task.Delay(workDelay, cancellationToken).ConfigureAwait(false);
                return await base.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref completedCount);
            }
        }
    }

    /// <summary>Engine that behaves like the tracking engine but fails after N completions.</summary>
    private sealed class FailAfterTrackingEngine(int failAfter, TimeSpan workDelay) : FakeTtsEngine
    {
        private int inFlight;
        private int peakConcurrency;
        private int completedCount;
        private readonly object gate = new();

        public int PeakConcurrency => peakConcurrency;

        public int CompletedCount => completedCount;

        public override async Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref inFlight);
            lock (gate)
            {
                if (current > peakConcurrency)
                {
                    peakConcurrency = current;
                }
            }

            try
            {
                await Task.Delay(workDelay, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Increment(ref completedCount) > failAfter)
                {
                    throw new InvalidOperationException("Synthesis failure after threshold.");
                }

                return await base.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }
    }
}
