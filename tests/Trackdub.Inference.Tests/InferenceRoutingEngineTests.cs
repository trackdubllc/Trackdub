using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Onnx.Translation;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class InferenceRoutingEngineTests
{
    [Fact]
    public async Task RoutedSpeechRegionDetector_SelectsAdapterFromPlannedEngineFamily()
    {
        var planner = new StubRuntimePlanner("silero-vad");
        var selected = new FakeSpeechRegionDetectorAdapter("silero-vad");
        var skipped = new FakeSpeechRegionDetectorAdapter("other-vad");
        var router = new RoutedSpeechRegionDetector(planner, [skipped, selected]);
        var options = new InferenceRequestOptions(PreferredModelAlias: "vad-choice");

        IReadOnlyList<SpeechRegion> regions = await router.DetectAsync(
            new SpeechRegionDetectionRequest("input.wav", 10, options),
            CancellationToken.None);

        Assert.Single(regions);
        Assert.Equal(1, selected.CallCount);
        Assert.Equal(0, skipped.CallCount);
        Assert.Same(options, selected.LastRequest?.Options);
        Assert.Equal(RuntimeStage.Vad, planner.LastRequest?.Stage);

        Assert.Equal("vad-choice", planner.LastRequest?.PreferredModelAlias);
        Assert.Equal(1, planner.PlanCallCount);
        Assert.Same(planner.LastPlan, selected.LastPlan);
        Assert.Equal(selected.LastExecutionSummary, router.LastExecutionSummary);
    }

    [Fact]
    public async Task RoutedAudioTranscriptionEngine_SelectsAdapterFromPlannedEngineFamily()
    {
        var planner = new StubRuntimePlanner("whisper-onnx");
        var selected = new FakeAudioTranscriptionEngineAdapter("whisper-onnx");
        var skipped = new FakeAudioTranscriptionEngineAdapter("whisper-genai");
        var router = new RoutedAudioTranscriptionEngine(planner, [skipped, selected]);
        var options = new InferenceRequestOptions(PreferredModelAlias: "asr-choice");

        IReadOnlyList<RecognizedTranscriptSegment> segments = await router.TranscribeAsync(
            new AudioTranscriptionRequest("input.wav", [new SpeechRegion(0, 0, 1)], options, SourceLanguage: "es"),
            CancellationToken.None);

        Assert.Single(segments);
        Assert.Equal(1, selected.CallCount);
        Assert.Equal(0, skipped.CallCount);
        Assert.Same(options, selected.LastRequest?.Options);
        Assert.Equal("es", selected.LastRequest?.SourceLanguage);
        Assert.Equal(RuntimeStage.Asr, planner.LastRequest?.Stage);

        Assert.Equal("asr-choice", planner.LastRequest?.PreferredModelAlias);
        Assert.Equal("es", planner.LastRequest?.SourceLanguage);
        Assert.Equal(1, planner.PlanCallCount);
        Assert.Same(planner.LastPlan, selected.LastPlan);
        Assert.Equal(selected.LastExecutionSummary, router.LastExecutionSummary);
    }

    [Fact]
    public async Task RoutedAudioTranscriptionEngine_SelectsNemotronAdapterFromPlannedEngineFamily()
    {
        var planner = new StubRuntimePlanner("nemotron-asr");
        var selected = new FakeAudioTranscriptionEngineAdapter("nemotron-asr");
        var skipped = new FakeAudioTranscriptionEngineAdapter("qwen3-asr");
        var router = new RoutedAudioTranscriptionEngine(planner, [skipped, selected]);

        IReadOnlyList<RecognizedTranscriptSegment> segments = await router.TranscribeAsync(
            new AudioTranscriptionRequest(
                "input.wav",
                [new SpeechRegion(0, 0, 1)],
                new InferenceRequestOptions(PreferredModelAlias: "nemotron-3.5-asr")),
            CancellationToken.None);

        Assert.Single(segments);
        Assert.Equal(1, selected.CallCount);
        Assert.Equal(0, skipped.CallCount);
        Assert.Equal("nemotron-3.5-asr", planner.LastRequest?.PreferredModelAlias);
        Assert.Same(planner.LastPlan, selected.LastPlan);
    }

    [Fact]
    public async Task RoutedSpeechRegionDetector_LegacyOverloadPreservesCommercialSafeModeOff()
    {
        var planner = new StubRuntimePlanner("silero-vad");
        var selected = new FakeSpeechRegionDetectorAdapter("silero-vad");
        var router = new RoutedSpeechRegionDetector(planner, [selected]);

        await router.DetectAsync("input.wav", 10, CancellationToken.None);

        Assert.Equal(RuntimeStage.Vad, planner.LastRequest?.Stage);

        Assert.Null(planner.LastRequest?.PreferredModelAlias);
    }

    [Fact]
    public async Task RoutedAudioTranscriptionEngine_WhenPlanRequiresDownload_DoesNotInvokeAdapter()
    {
        var planner = new FixedRuntimePlanner(CreateDownloadRequiredPlan(
            RuntimeStage.Asr,
            "onnx-community/whisper-small",
            "whisper-small",
            "whisper-small",
            "Machine-local cache does not contain 'onnx/model_fp16.onnx' for model 'onnx-community/whisper-small'."));
        var adapter = new FakeAudioTranscriptionEngineAdapter("whisper-small");
        var router = new RoutedAudioTranscriptionEngine(planner, [adapter]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.TranscribeAsync(
                new AudioTranscriptionRequest("input.wav", [new SpeechRegion(0, 0, 1)]),
                CancellationToken.None));

        Assert.Equal(0, adapter.CallCount);
        Assert.Equal(1, planner.PlanCallCount);
    }

    [Fact]
    public async Task RoutedSpeechRegionDetector_WhenPlanRequiresDownload_DoesNotInvokeAdapter()
    {
        var planner = new FixedRuntimePlanner(CreateDownloadRequiredPlan(
            RuntimeStage.Vad,
            "onnx-community/silero-vad",
            "silero-vad",
            "silero-vad",
            "Machine-local cache does not contain 'onnx/model_fp16.onnx' for model 'onnx-community/silero-vad'."));
        var adapter = new FakeSpeechRegionDetectorAdapter("silero-vad");
        var router = new RoutedSpeechRegionDetector(planner, [adapter]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.DetectAsync(
                new SpeechRegionDetectionRequest("input.wav", 10),
                CancellationToken.None));

        Assert.Equal(0, adapter.CallCount);
        Assert.Equal(1, planner.PlanCallCount);
    }

    [Fact]
    public async Task RoutedAudioTranscriptionEngine_LegacyOverloadPreservesCommercialSafeModeOff()
    {
        var planner = new StubRuntimePlanner("whisper-onnx");
        var selected = new FakeAudioTranscriptionEngineAdapter("whisper-onnx");
        var router = new RoutedAudioTranscriptionEngine(planner, [selected]);

        await router.TranscribeAsync("input.wav", [new SpeechRegion(0, 0, 1)], CancellationToken.None);

        Assert.Equal(RuntimeStage.Asr, planner.LastRequest?.Stage);

        Assert.Null(planner.LastRequest?.PreferredModelAlias);
    }

    [Fact]
    public async Task RoutedSpeakerDiarizationEngine_SelectsAdapterFromPlannedEngineFamily()
    {
        var planner = new StubRuntimePlanner("sortformer");
        var selected = new FakeSpeakerDiarizationEngineAdapter("sortformer");
        var skipped = new FakeSpeakerDiarizationEngineAdapter("other-diarization");
        var router = new RoutedSpeakerDiarizationEngine(planner, [skipped, selected]);
        var options = new InferenceRequestOptions(PreferredModelAlias: "diarization-choice");

        IReadOnlyList<DiarizedSpeakerTurn> turns = await router.DiarizeAsync(
            new SpeakerDiarizationRequest("input.wav", 10, [new SpeechRegion(0, 0, 1)], options),
            CancellationToken.None);

        Assert.Single(turns);
        Assert.Equal(1, selected.CallCount);
        Assert.Equal(0, skipped.CallCount);
        Assert.Same(options, selected.LastRequest?.Options);
        Assert.Equal(RuntimeStage.Diarization, planner.LastRequest?.Stage);

        Assert.Equal("diarization-choice", planner.LastRequest?.PreferredModelAlias);
        Assert.Equal(1, planner.PlanCallCount);
        Assert.Same(planner.LastPlan, selected.LastPlan);
        Assert.Equal(selected.LastExecutionSummary, router.LastExecutionSummary);
    }

    [Fact]
    public async Task RoutedStemSeparationEngine_SelectsAdapterFromPlannedEngineFamily()
    {
        var planner = new StubRuntimePlanner("generic-separator");
        var selected = new FakeStemSeparationEngineAdapter("generic-separator");
        var skipped = new FakeStemSeparationEngineAdapter("other-separation");
        var router = new RoutedStemSeparationEngine(planner, [skipped, selected]);
        var request = new StemSeparationRequest(
            "source.wav",
            "vocals.wav",
            "ambiance.wav",
            PreferredModelAlias: "separation-choice");

        StemSeparationResult result = await router.SeparateAsync(request, progress: null, CancellationToken.None);

        Assert.Equal(10, result.DurationSeconds);
        Assert.Equal(1, selected.CallCount);
        Assert.Equal(0, skipped.CallCount);
        Assert.Same(request, selected.LastRequest);
        Assert.Equal(RuntimeStage.Separation, planner.LastRequest?.Stage);

        Assert.Equal("separation-choice", planner.LastRequest?.PreferredModelAlias);
        Assert.Equal(1, planner.PlanCallCount);
        Assert.Same(planner.LastPlan, selected.LastPlan);
        Assert.Equal(selected.LastExecutionSummary, router.LastExecutionSummary);
    }

    [Fact]
    public async Task RoutedTtsEngine_SelectsAdapterFromPlannedEngineFamily()
    {
        var planner = new StubRuntimePlanner("kokoro");
        var selected = new FakeTtsEngineAdapter("kokoro");
        var skipped = new FakeTtsEngineAdapter("other-tts");
        var router = new RoutedTtsEngine(planner, [skipped, selected]);
        var options = new InferenceRequestOptions(PreferredModelAlias: "tts-choice");
        var request = new TtsSynthesisRequest(
            "hello",
            "en",
            new VoiceCatalogEntry("voice", "en", "neutral", "Voice"),
            Options: options);

        TtsSynthesisResult result = await router.SynthesizeAsync(request, CancellationToken.None);

        Assert.Equal("voice", result.VoiceId);
        Assert.Equal(1, selected.CallCount);
        Assert.Equal(0, skipped.CallCount);
        Assert.Same(request, selected.LastRequest);
        Assert.Equal(RuntimeStage.Tts, planner.LastRequest?.Stage);

        Assert.Equal("tts-choice", planner.LastRequest?.PreferredModelAlias);
        Assert.Equal("en", planner.LastRequest?.SourceLanguage);
        Assert.Equal(1, planner.PlanCallCount);
        Assert.Same(planner.LastPlan, selected.LastPlan);

        // Summary is derived structurally from the plan (not from the adapter's mutable
        // LastExecutionSummary), so it survives parallel synthesis without races.
        StageRuntimeExecutionSummary? summary = router.LastExecutionSummary;
        Assert.NotNull(summary);
        Assert.Equal("kokoro-model", summary!.ModelId);
        Assert.Equal("kokoro-alias", summary.ModelAlias);
        Assert.Equal("default", summary.ModelVariant);
        Assert.Equal(ExecutionProviderKind.Cpu.ToString(), summary.SelectedProvider);
    }

    [Fact]
    public async Task RoutedTranslationEngine_SelectsAdapterFromRouteEngineFamily()
    {
        var route = new TranslationRouteSelection(
            "en",
            "fr",
            TranslationRoutingKind.Pivot,
            IsAvailable: true,
            ProviderName: "madlad400",
            RouteDetail: "MADLAD-400 pivot",
            ModelId: "madlad-model",
            PreferredModelAlias: "madlad400-mt",
            EngineFamily: "madlad");
        var languageRouter = new StubTranslationLanguageRouter(route);
        var selected = new FakeTranslationEngineAdapter("madlad");
        var skipped = new FakeTranslationEngineAdapter("opus-mt");
        var router = new RoutedTranslationEngine(languageRouter, [skipped, selected]);

        IReadOnlyList<TranslatedTextSegment> translated = await router.TranslateAsync(
            new TranslationRequest(
                "en",
                "fr",
                [new TranslationInputSegment(0, 0, 1, "hello")],
                PreferredModelAlias: "user-choice"),
            CancellationToken.None);

        Assert.Single(translated);
        Assert.Equal(1, selected.CallCount);
        Assert.Equal(0, skipped.CallCount);
        Assert.Equal("madlad400-mt", selected.LastRequest?.PreferredModelAlias);
        Assert.Equal("user-choice", languageRouter.LastPreferredModelAlias);
        Assert.Equal(selected.LastExecutionSummary, router.LastExecutionSummary);
        Assert.Equal("madlad400", router.LastExecutionMetadata?.ProviderName);
        Assert.Equal(TranslationRoutingKind.Pivot, router.LastExecutionMetadata?.RoutingKind);
    }

    private static StageRuntimePlan CreateDownloadRequiredPlan(
        RuntimeStage stage,
        string modelId,
        string modelAlias,
        string engineFamily,
        string fallbackDetail) =>
        new StageRuntimePlan
        {
            Stage = stage,
            Status = StageRuntimePlanStatus.DownloadRequired,
            ModelId = modelId,
            ModelAlias = modelAlias,
            EngineFamily = engineFamily,
            Variant = "fp16",
            ExecutionProvider = ExecutionProviderKind.DirectMl,
            Fallback = new RuntimePlanFallback(RuntimePlanFallbackCode.ModelNotCached, fallbackDetail)
        };

    private sealed class StubRuntimePlanner(string engineFamily) : IRuntimePlanner
    {
        public StageRuntimePlanningRequest? LastRequest { get; private set; }

        public StageRuntimePlan? LastPlan { get; private set; }

        public int PlanCallCount { get; private set; }

        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            PlanCallCount++;
            LastPlan = new StageRuntimePlan
            {
                Stage = request.Stage,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = $"{engineFamily}-model",
                ModelAlias = $"{engineFamily}-alias",
                EngineFamily = engineFamily,
                Variant = "default",
                ExecutionProvider = ExecutionProviderKind.Cpu
            };
            return Task.FromResult(LastPlan);
        }
    }

    private sealed class StubTranslationLanguageRouter(TranslationRouteSelection route) : ITranslationLanguageRouter
    {
        public string? LastPreferredModelAlias { get; private set; }

        public Task<IReadOnlyList<TranslationTargetLanguageOption>> GetSupportedTargetLanguagesAsync(
            string sourceLanguage,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslationTargetLanguageOption>>([]);

        public Task<TranslationRouteSelection> ResolveRouteAsync(
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken,
            string? preferredModelAlias = null)
        {
            LastPreferredModelAlias = preferredModelAlias;
            return Task.FromResult(route);
        }
    }

    private sealed class FixedRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
    {
        public int PlanCallCount { get; private set; }

        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            PlanCallCount++;
            return Task.FromResult(plan);
        }
    }

    private sealed class FakeSpeechRegionDetectorAdapter(string engineFamily) : ISpeechRegionDetectorAdapter, IStageRuntimeExecutionReporter
    {
        public string EngineFamily => engineFamily;

        public int CallCount { get; private set; }

        public SpeechRegionDetectionRequest? LastRequest { get; private set; }

        public StageRuntimePlan? LastPlan { get; private set; }

        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

        public Task<IReadOnlyList<SpeechRegion>> DetectAsync(
            string normalizedAudioPath,
            double durationSeconds,
            CancellationToken cancellationToken) =>
            DetectAsync(new SpeechRegionDetectionRequest(normalizedAudioPath, durationSeconds), cancellationToken);

        public Task<IReadOnlyList<SpeechRegion>> DetectAsync(
            SpeechRegionDetectionRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult<IReadOnlyList<SpeechRegion>>([new SpeechRegion(0, 0, 1)]);
        }

        public Task<IReadOnlyList<SpeechRegion>> DetectAsync(
            SpeechRegionDetectionRequest request,
            StageRuntimePlan plan,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastPlan = plan;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult<IReadOnlyList<SpeechRegion>>([new SpeechRegion(0, 0, 1)]);
        }
    }

    private sealed class FakeAudioTranscriptionEngineAdapter(string engineFamily) : IAudioTranscriptionEngineAdapter, IStageRuntimeExecutionReporter
    {
        public string EngineFamily => engineFamily;

        public int CallCount { get; private set; }

        public AudioTranscriptionRequest? LastRequest { get; private set; }

        public StageRuntimePlan? LastPlan { get; private set; }

        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken) =>
            TranscribeAsync(new AudioTranscriptionRequest(normalizedAudioPath, regions), cancellationToken);

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            AudioTranscriptionRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>(
                [new RecognizedTranscriptSegment(0, 0, 1, "hello")]);
        }

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            AudioTranscriptionRequest request,
            StageRuntimePlan plan,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastPlan = plan;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>(
                [new RecognizedTranscriptSegment(0, 0, 1, "hello")]);
        }
    }

    private sealed class FakeSpeakerDiarizationEngineAdapter(string engineFamily) : ISpeakerDiarizationEngineAdapter, IStageRuntimeExecutionReporter
    {
        public string EngineFamily => engineFamily;

        public int CallCount { get; private set; }

        public SpeakerDiarizationRequest? LastRequest { get; private set; }

        public StageRuntimePlan? LastPlan { get; private set; }

        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

        public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
            string normalizedAudioPath,
            double durationSeconds,
            IReadOnlyList<SpeechRegion> speechRegions,
            CancellationToken cancellationToken) =>
            DiarizeAsync(
                new SpeakerDiarizationRequest(
                    normalizedAudioPath,
                    durationSeconds,
                    speechRegions,
                    InferenceRequestOptions.Default),
                cancellationToken);

        public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
            SpeakerDiarizationRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult<IReadOnlyList<DiarizedSpeakerTurn>>(
                [new DiarizedSpeakerTurn("speaker-1", 0, 1)]);
        }

        public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
            SpeakerDiarizationRequest request,
            StageRuntimePlan plan,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastPlan = plan;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult<IReadOnlyList<DiarizedSpeakerTurn>>(
                [new DiarizedSpeakerTurn("speaker-1", 0, 1)]);
        }
    }

    private sealed class FakeStemSeparationEngineAdapter(string engineFamily) : IStemSeparationEngineAdapter, IStageRuntimeExecutionReporter
    {
        public string EngineFamily => engineFamily;

        public int CallCount { get; private set; }

        public StemSeparationRequest? LastRequest { get; private set; }

        public StageRuntimePlan? LastPlan { get; private set; }

        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

        public Task<StemSeparationResult> SeparateAsync(
            StemSeparationRequest request,
            IProgress<StemSeparationProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult(new StemSeparationResult(10, 44100, 2));
        }

        public Task<StemSeparationResult> SeparateAsync(
            StemSeparationRequest request,
            StageRuntimePlan plan,
            IProgress<StemSeparationProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastPlan = plan;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult(new StemSeparationResult(10, 44100, 2));
        }
    }

    private sealed class FakeTtsEngineAdapter(string engineFamily) : ITtsEngineAdapter, IStageRuntimeExecutionReporter
    {
        public string EngineFamily => engineFamily;

        public int CallCount { get; private set; }

        public TtsSynthesisRequest? LastRequest { get; private set; }

        public StageRuntimePlan? LastPlan { get; private set; }

        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

        public Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult(new TtsSynthesisResult([], 0, 24000, $"{engineFamily}-model", request.Voice.VoiceId, engineFamily));
        }

        public Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            StageRuntimePlan plan,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastPlan = plan;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult(new TtsSynthesisResult([], 0, 24000, $"{engineFamily}-model", request.Voice.VoiceId, engineFamily));
        }
    }

    private sealed class FakeTranslationEngineAdapter(string engineFamily) : ITranslationEngineAdapter, IStageRuntimeExecutionReporter
    {
        public string EngineFamily => engineFamily;

        public int CallCount { get; private set; }

        public TranslationRequest? LastRequest { get; private set; }

        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

        public Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            LastExecutionSummary = CreateSummary(engineFamily);
            return Task.FromResult<IReadOnlyList<TranslatedTextSegment>>(
                request.Segments
                    .Select(segment => new TranslatedTextSegment(segment.Index, segment.StartSeconds, segment.EndSeconds, $"translated:{segment.Text}"))
                    .ToArray());
        }
    }

    private static StageRuntimeExecutionSummary CreateSummary(string engineFamily) =>
        new(
            "auto",
            "cpu",
            $"{engineFamily}-model",
            $"{engineFamily}-alias",
            "default",
            $"{engineFamily} adapter");
}
