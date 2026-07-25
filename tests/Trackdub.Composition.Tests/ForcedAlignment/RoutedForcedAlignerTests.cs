using Trackdub.Composition.ForcedAlignment;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Composition.Tests.ForcedAlignment;

public sealed class RoutedForcedAlignerTests
{
    private static ForcedAlignmentRequest MakeRequest(ForcedAlignmentOptions? options = null) => new(
        AudioPath: "/tmp/clip.wav",
        NormalizedTranscript: "hello",
        LanguageCode: null,
        SegmentId: "seg-1",
        Options: options ?? new ForcedAlignmentOptions());

    [Fact]
    public async Task AlignAsync_NoAdaptersAvailable_SkipsWithInstallReason()
    {
        var router = new RoutedForcedAligner([new StubAlignerAdapter("a", "model-a") { Available = false }]);

        ForcedAlignmentResult result = await router.AlignAsync(
            MakeRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(ForcedAlignmentStatus.Skipped, result.Status);
        Assert.Contains("No forced-alignment model is installed", result.SkipReason);
        Assert.Equal("none", result.ProviderId);
    }

    [Fact]
    public async Task AlignAsync_DefaultOptions_UsesRegistrationOrder()
    {
        var first = new StubAlignerAdapter("first", "model-first") { Available = true, SupportsPhonemes = false };
        var second = new StubAlignerAdapter("second", "model-second") { Available = true, SupportsPhonemes = true };
        var router = new RoutedForcedAligner([first, second]);

        await router.AlignAsync(MakeRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(1, first.CallCount);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public async Task AlignAsync_RequirePhonemeTimings_RoutesPastWordLevelAdapter()
    {
        // Word-level adapter registered FIRST (the pre-fix failure mode: Qwen wins by
        // registration order and lip-sync gets zero phonemes).
        var wordLevel = new StubAlignerAdapter("qwen", "qwen3-forced-aligner-0.6b-q4-onnx")
        { Available = true, SupportsPhonemes = false };
        var phonemeCapable = new StubAlignerAdapter("ctc", "wav2vec2-lv60-espeak-cv-ft-onnx")
        { Available = true, SupportsPhonemes = true };
        var router = new RoutedForcedAligner([wordLevel, phonemeCapable]);

        ForcedAlignmentResult result = await router.AlignAsync(
            MakeRequest(new ForcedAlignmentOptions(RequirePhonemeTimings: true)),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, wordLevel.CallCount);
        Assert.Equal(1, phonemeCapable.CallCount);
        Assert.Equal("wav2vec2-lv60-espeak-cv-ft-onnx", result.ModelId);
    }

    [Fact]
    public async Task AlignAsync_RequirePhonemeTimings_OnlyWordLevelInstalled_SkipsWithPhonemeReason()
    {
        var wordLevel = new StubAlignerAdapter("qwen", "qwen3-forced-aligner-0.6b-q4-onnx")
        { Available = true, SupportsPhonemes = false };
        var router = new RoutedForcedAligner([wordLevel]);

        ForcedAlignmentResult result = await router.AlignAsync(
            MakeRequest(new ForcedAlignmentOptions(RequirePhonemeTimings: true)),
            TestContext.Current.CancellationToken);

        Assert.Equal(ForcedAlignmentStatus.Skipped, result.Status);
        Assert.Contains("phoneme-capable", result.SkipReason);
        Assert.Empty(result.Phonemes);

        // The word-level adapter must never run: that would fake phoneme readiness.
        Assert.Equal(0, wordLevel.CallCount);
    }

    [Fact]
    public async Task AlignAsync_PreferredModelAlias_SelectsMatchingAdapterOverFirst()
    {
        var first = new StubAlignerAdapter("first", "model-first") { Available = true, SupportsPhonemes = true };
        var preferred = new StubAlignerAdapter("ctc", "wav2vec2-lv60-espeak-cv-ft-onnx")
        { Available = true, SupportsPhonemes = true };
        var router = new RoutedForcedAligner([first, preferred]);

        await router.AlignAsync(
            MakeRequest(new ForcedAlignmentOptions(PreferredModelAlias: "wav2vec2-lv60-espeak-cv-ft-onnx")),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, first.CallCount);
        Assert.Equal(1, preferred.CallCount);
    }

    [Fact]
    public async Task AlignAsync_PreferredWordLevelWithRequirePhonemeTimings_FallsBackToPhonemeCapable()
    {
        var wordLevel = new StubAlignerAdapter("qwen", "qwen3-forced-aligner-0.6b-q4-onnx")
        { Available = true, SupportsPhonemes = false };
        var phonemeCapable = new StubAlignerAdapter("ctc", "wav2vec2-lv60-espeak-cv-ft-onnx")
        { Available = true, SupportsPhonemes = true };
        var router = new RoutedForcedAligner([wordLevel, phonemeCapable]);

        await router.AlignAsync(
            MakeRequest(new ForcedAlignmentOptions(
                RequirePhonemeTimings: true,
                PreferredModelAlias: "qwen3-forced-aligner-0.6b-q4-onnx")),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, wordLevel.CallCount);
        Assert.Equal(1, phonemeCapable.CallCount);
    }

    [Fact]
    public async Task AlignAsync_PreferredModelAliasUnavailable_FallsBackToCapabilitySelection()
    {
        var wordLevel = new StubAlignerAdapter("qwen", "qwen3-forced-aligner-0.6b-q4-onnx")
        { Available = true, SupportsPhonemes = false };
        var phonemeCapable = new StubAlignerAdapter("ctc", "wav2vec2-lv60-espeak-cv-ft-onnx")
        { Available = true, SupportsPhonemes = true };
        var router = new RoutedForcedAligner([wordLevel, phonemeCapable]);

        await router.AlignAsync(
            MakeRequest(new ForcedAlignmentOptions(
                RequirePhonemeTimings: true,
                PreferredModelAlias: "some-model-that-is-not-installed")),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, wordLevel.CallCount);
        Assert.Equal(1, phonemeCapable.CallCount);
    }

    [Fact]
    public async Task AlignAsync_AdapterThrows_ReturnsStructuredFailedResult()
    {
        var adapter = new StubAlignerAdapter("boom", "boom-model")
        { Available = true, SupportsPhonemes = true, ThrowOnAlign = true };
        var router = new RoutedForcedAligner([adapter]);

        ForcedAlignmentResult result = await router.AlignAsync(
            MakeRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(ForcedAlignmentStatus.Failed, result.Status);
        Assert.Equal("boom-model", result.ModelId);
    }

    private sealed class StubAlignerAdapter(string providerId, string modelId) : IForcedAlignerAdapter
    {
        public string ProviderId => providerId;
        public string ModelId => modelId;
        public bool Available { get; init; }
        public bool SupportsPhonemes { get; init; }
        public bool ThrowOnAlign { get; init; }
        public int CallCount { get; private set; }

        public bool IsAvailable => Available;
        public bool SupportsPhonemeTimings => SupportsPhonemes;

        public Task<ForcedAlignmentResult> AlignAsync(
            ForcedAlignmentRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowOnAlign)
                throw new InvalidOperationException("StubAlignerAdapter: simulated failure.");

            return Task.FromResult(new ForcedAlignmentResult(
                SegmentId: request.SegmentId,
                Status: ForcedAlignmentStatus.Success,
                Words: [],
                Phonemes: [new PhonemeTiming("AH", "espeak-ipa", TimeSpan.Zero, TimeSpan.FromMilliseconds(80), 0.9)],
                Confidence: new AlignmentConfidence(0.9, null, null),
                SkipReason: null,
                ProviderId: providerId,
                ModelId: modelId));
        }
    }
}
