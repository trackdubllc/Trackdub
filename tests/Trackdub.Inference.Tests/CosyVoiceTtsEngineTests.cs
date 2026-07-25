using Trackdub.Application.Transcripts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.CosyVoice;
using Trackdub.Inference.Onnx.Kokoro;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

public sealed class CosyVoiceTtsEngineTests
{
    private const string ReferenceTranscript =
        "This is a longer reference transcript used to bootstrap a CosyVoice cloning clip.";
    private const string CloneTargetText = "CosyVoice should synthesize this translated line.";

    [Fact]
    public void CosyVoiceModelFiles_ResolvesDefaultVariantPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cosyvoice-default-{Guid.NewGuid():N}");
        var files = CosyVoiceModelFiles.Resolve(root, "default");

        Assert.Equal("default", files.Variant);
        Assert.Equal(Path.Combine(root, "llm", "text_encoder.onnx"), files.TextEncoderPath);
        Assert.Equal(Path.Combine(root, "flow.decoder.estimator.fp32.onnx"), files.FlowDecoderEstimatorPath);
        Assert.Equal(Path.Combine(root, "hift", "vocoder.onnx"), files.HiftVocoderPath);
    }

    [Fact]
    public void CosyVoiceModelFiles_ResolvesInt8VariantPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cosyvoice-variant-{Guid.NewGuid():N}");
        string int8Relative = Path.Combine("onnx_quantized_modelopt", "llm", "text_encoder.int8.onnx");
        string int8Path = Path.Combine(root, int8Relative);
        Directory.CreateDirectory(Path.GetDirectoryName(int8Path)!);
        File.WriteAllBytes(int8Path, []);

        try
        {
            var files = CosyVoiceModelFiles.Resolve(root, "int8");

            Assert.Equal("int8", files.Variant);
            Assert.Equal(int8Path, files.TextEncoderPath);
            Assert.Equal(Path.Combine(root, "flow.decoder.estimator.fp32.onnx"), files.FlowDecoderEstimatorPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CosyVoiceTtsEngine_RequiresConsent()
    {
        var engine = CreateEngine(granted: false);
        var plan = DefaultPlan();

        var request = new TtsSynthesisRequest(
            CloneTargetText,
            "en",
            new VoiceCatalogEntry("voice-clone:test", "en", "synthetic", "Clone"),
            VoiceCloneReference: new VoiceCloneReference(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "missing.wav",
                5,
                4,
                ReferenceTranscript: ReferenceTranscript));

        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            engine.SynthesizeAsync(request, plan, CancellationToken.None));
    }

    [Fact]
    public async Task CosyVoiceTtsEngine_RequiresReferenceTranscript()
    {
        var engine = CreateEngine();
        var plan = DefaultPlan();

        var request = new TtsSynthesisRequest(
            CloneTargetText,
            "en",
            new VoiceCatalogEntry("voice-clone:test", "en", "synthetic", "Clone"),
            VoiceCloneReference: new VoiceCloneReference(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "missing.wav",
                5,
                4));

        await Assert.ThrowsAsync<TtsReferenceTextRequiredException>(() =>
            engine.SynthesizeAsync(request, plan, CancellationToken.None));
    }

    [RequiresBundledModelFact(
        "cosyvoice-300m-onnx/llm/text_encoder.onnx",
        "cosyvoice-300m-onnx/llm/token_generator.onnx",
        "cosyvoice-300m-onnx/flow/encoder.onnx",
        "cosyvoice-300m-onnx/flow.decoder.estimator.fp32.onnx",
        "cosyvoice-300m-onnx/hift/vocoder.onnx",
        "cosyvoice-300m-onnx/tokenizer/tiktoken_ranks.bin",
        "kokoro-onnx/onnx/model.onnx",
        "kokoro-onnx/tokenizer.json",
        "kokoro-onnx/voices/af_heart.bin")]
    public async Task CosyVoiceTtsEngine_SynthesizesZeroShotWithBundledModel()
    {
        string referenceClipPath = await CreateReferenceClipAsync();

        try
        {
            var engine = CreateEngine();
            var plan = DefaultPlan();

            var request = new TtsSynthesisRequest(
                CloneTargetText,
                "en",
                new VoiceCatalogEntry("voice-clone:ref", "en", "synthetic", "Reference"),
                VoiceCloneReference: new VoiceCloneReference(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    referenceClipPath,
                    ReferenceTranscript.Length,
                    ReferenceTranscript.Length,
                    ReferenceTranscript: ReferenceTranscript));

            TtsSynthesisResult result = await engine.SynthesizeAsync(request, plan, CancellationToken.None);

            AssertCloneResult(result);
            Assert.NotNull(engine.LastExecutionSummary);
        }
        finally
        {
            TryDelete(referenceClipPath);
        }
    }

    [RequiresBundledModelFact(
        "cosyvoice-300m-onnx/llm/text_encoder.onnx",
        "cosyvoice-300m-onnx/onnx_quantized_modelopt/llm/text_encoder.int8.onnx",
        "kokoro-onnx/onnx/model.onnx",
        "kokoro-onnx/tokenizer.json",
        "kokoro-onnx/voices/af_heart.bin")]
    public async Task CosyVoiceTtsEngine_SynthesizesInt8VariantWhenPresent()
    {
        string referenceClipPath = await CreateReferenceClipAsync();

        try
        {
            var engine = CreateEngine();
            var plan = DefaultPlan(variant: "int8");

            var request = new TtsSynthesisRequest(
                CloneTargetText,
                "en",
                new VoiceCatalogEntry("voice-clone:ref", "en", "synthetic", "Reference"),
                VoiceCloneReference: new VoiceCloneReference(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    referenceClipPath,
                    ReferenceTranscript.Length,
                    ReferenceTranscript.Length,
                    ReferenceTranscript: ReferenceTranscript));

            TtsSynthesisResult result = await engine.SynthesizeAsync(request, plan, CancellationToken.None);
            AssertCloneResult(result);
        }
        finally
        {
            TryDelete(referenceClipPath);
        }
    }

    private static CosyVoiceTtsEngine CreateEngine(bool granted = true) =>
        new(new StubConsentService(granted), BenchmarkModelPathResolver.CreateDefault());

    private static StageRuntimePlan DefaultPlan(
        string variant = "default",
        ExecutionProviderKind executionProvider = ExecutionProviderKind.Cpu) => new()
        {
            Stage = RuntimeStage.Tts,
            Status = StageRuntimePlanStatus.Ready,
            ModelId = "tonythethompson/CosyVoice-300M-ONNX",
            ModelAlias = CosyVoiceDefaults.PrimaryAlias,
            EngineFamily = CosyVoiceTtsEngine.EngineFamilyName,
            Variant = variant,
            ExecutionProvider = executionProvider
        };

    private static async Task<string> CreateReferenceClipAsync()
    {
        var kokoro = new KokoroTtsEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Tts,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "onnx-community/Kokoro-82M-v1.0-ONNX",
                ModelAlias = "kokoro-onnx",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault(),
            new StubPhonemizer("ðɪs ɪz ə lɔŋɚ ɹɛfɚəns klɪp fɔɹ kɑzi vɔɪs klounɪŋ"));

        TtsSynthesisResult synthesized = await kokoro.SynthesizeAsync(
            new TtsSynthesisRequest(
                ReferenceTranscript,
                "en-us",
                new VoiceCatalogEntry("af_heart", "en-us", "female", "Heart")),
            CancellationToken.None);

        string tempPath = Path.Combine(Path.GetTempPath(), $"cosyvoice_ref_{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(tempPath, synthesized.WavBytes);
        return tempPath;
    }

    private static void AssertCloneResult(TtsSynthesisResult result)
    {
        Assert.True(result.WavBytes.Length > 44);
        Assert.Equal(22_050, result.SampleRate);
        Assert.True(result.DurationSamples > 0);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class StubConsentService(bool granted) : IConsentService
    {
        public Guid SessionId { get; } = Guid.NewGuid();

        public bool IsVoiceCloningConsentGranted => granted;

        public event EventHandler? VoiceCloningConsentChanged;

        public void GrantVoiceCloningConsent() => VoiceCloningConsentChanged?.Invoke(this, EventArgs.Empty);

        public void ClearVoiceCloningConsent() => VoiceCloningConsentChanged?.Invoke(this, EventArgs.Empty);
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

        public string Phonemize(string text, string languageCode) => factory();
    }
}
