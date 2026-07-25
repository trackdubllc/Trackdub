using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Kokoro;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

public sealed class KokoroTtsEngineTests
{
    private static VoiceCatalogEntry TestVoice => new("af_heart", "en-us", "female", "Heart");

    [RequiresBundledModelFact("kokoro-onnx/onnx/model.onnx", "kokoro-onnx/tokenizer.json", "kokoro-onnx/voices/af_heart.bin")]
    public async Task KokoroTtsEngine_SynthesizesWithBundledModel()
    {
        var engine = new KokoroTtsEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Tts,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "onnx-community/Kokoro-82M-v1.0-ONNX",
                ModelAlias = "kokoro-onnx",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault(),
            new StubPhonemizer("həlˈoʊ wɜːld")); // pre-phonemized avoids espeak-ng dependency in CI

        var request = new TtsSynthesisRequest("Hello world", "en-us", TestVoice);

        TtsSynthesisResult result = await engine.SynthesizeAsync(request, CancellationToken.None);

        Assert.True(result.WavBytes.Length > 44);
        Assert.Equal((byte)'R', result.WavBytes[0]);
        Assert.Equal((byte)'I', result.WavBytes[1]);
        Assert.Equal((byte)'F', result.WavBytes[2]);
        Assert.Equal((byte)'F', result.WavBytes[3]);
        Assert.Equal(24_000, result.SampleRate);
        Assert.True(result.DurationSamples > 0);
        Assert.Equal("af_heart", result.VoiceId);
        Assert.Equal("cpu", result.Provider);
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
    }

    [RequiresBundledModelFact("kokoro-onnx/onnx/model.onnx", "kokoro-onnx/tokenizer.json", "kokoro-onnx/voices/af_heart.bin")]
    public async Task KokoroTtsEngine_PhonemeOverride_SkipsG2P()
    {
        var phonemizerThatMustNotBeCalled = new StubPhonemizer(
            () => throw new InvalidOperationException("G2P must not be called when PhonemeOverride is set."));
        var engine = new KokoroTtsEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Tts,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "onnx-community/Kokoro-82M-v1.0-ONNX",
                ModelAlias = "kokoro-onnx",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault(),
            phonemizerThatMustNotBeCalled);

        var request = new TtsSynthesisRequest(
            "Hello world", "en-us", TestVoice,
            PhonemeOverride: "həlˈoʊ wɜːld");

        TtsSynthesisResult result = await engine.SynthesizeAsync(request, CancellationToken.None);

        Assert.True(result.DurationSamples > 0);
        Assert.False(phonemizerThatMustNotBeCalled.WasCalled);
    }

    private sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
    {
        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan with { Stage = request.Stage });
    }

    private sealed class StubPhonemizer : IGraphemeToPhoneme
    {
        private readonly Func<string> factory;

        public StubPhonemizer(string fixedPhonemes) => factory = () => fixedPhonemes;
        public StubPhonemizer(Func<string> factory) => this.factory = factory;

        public bool WasCalled { get; private set; }

        public string Phonemize(string text, string languageCode)
        {
            WasCalled = true;
            return factory();
        }
    }
}
