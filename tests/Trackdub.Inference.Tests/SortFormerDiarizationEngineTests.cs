using System.Buffers.Binary;
using Trackdub.Composition.Runtime.Planning;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.SortFormer;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class SortFormerDiarizationEngineTests
{
    [Fact]
    public void MaxSupportedSpeakers_IsPublicConstantEqualToFour()
    {
        Assert.Equal(4, SortFormerDiarizationEngine.MaxSupportedSpeakers);
    }

    [Fact]
    public void SortFormerFeatureExtractor_extracts_finite_streaming_features()
    {
        float[] samples = CreateSineWave(durationSeconds: 1.0, sampleRate: 16000);
        var extractor = new SortFormerFeatureExtractor();

        using SortFormerFeatureInputSet features = extractor.Extract(samples);

        Assert.True(features.FrameCount > 0);
        Assert.Equal(128, features.FeatureCount);
        Assert.Equal(features.FrameCount * features.FeatureCount, features.Data.Length);
        foreach (float value in features.Data)
        {
            Assert.True(float.IsFinite(value));
        }
    }

    [LocalSortFormerModelFact]
    public async Task DiarizeAsync_with_local_cached_v21_onnx_accepts_streaming_feature_inputs()
    {
        BundledModelManifestRegistry registry = LoadRegistry();
        string modelPath = GetLocalCacheModelPath();
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string wavePath = Path.Combine(tempDirectory, "sortformer-local-cache.wav");

        try
        {
            await WriteTestWaveAsync(wavePath, durationSeconds: 0.5, sampleRate: 16000, CancellationToken.None);
            var engine = new SortFormerDiarizationEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Diarization,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "cgus/diar_streaming_sortformer_4spk-v2.1-onnx",
                    ModelAlias = "sortformer-diarizer-4spk-v2.1",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu,
                    ModelEntryPath = modelPath
                }),
                new BenchmarkModelPathResolver(registry));

            IReadOnlyList<DiarizedSpeakerTurn> turns = await engine.DiarizeAsync(
                wavePath,
                0.5,
                [new SpeechRegion(0, 0.0, 0.5)],
                CancellationToken.None);

            Assert.NotNull(turns);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [SortFormerFixtureFact]
    public async Task DiarizeAsync_with_real_sortformer_fixture_produces_valid_turns()
    {
        BundledModelManifestRegistry registry = LoadRegistry();
        _ = ResolveFixtureModelPath(registry)
            ?? throw new InvalidOperationException("SortFormer ONNX fixture resolution unexpectedly failed after attribute validation.");

        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string wavePath = Path.Combine(tempDirectory, "sortformer-fixture.wav");

        try
        {
            await WriteTestWaveAsync(wavePath, durationSeconds: 1.0, sampleRate: 16000, CancellationToken.None);
            var runtimePlanner = new RuntimePlanner(
                registry,
                new MachineHardwareProfileProvider(),
                new OnnxExecutionProviderDiscovery(new NullOpenVinoAvailabilityProvider()),
                new OnnxExecutionProviderSmokeTester(),
                new CompositeModelCacheInventory(
                    new BundledManifestModelCacheInventory(registry)));
            var engine = new SortFormerDiarizationEngine(
                runtimePlanner,
                new BenchmarkModelPathResolver(registry));

            IReadOnlyList<DiarizedSpeakerTurn> turns = await engine.DiarizeAsync(
                wavePath,
                1.0,
                [new SpeechRegion(0, 0.0, 1.0)],
                CancellationToken.None);

            Assert.NotNull(turns);

            foreach (DiarizedSpeakerTurn turn in turns)
            {
                Assert.True(turn.EndSeconds > turn.StartSeconds, $"Turn end ({turn.EndSeconds}) must be greater than start ({turn.StartSeconds}).");
                Assert.True(turn.EndSeconds <= 1.0, $"Turn end ({turn.EndSeconds}) must not exceed audio duration (1.0).");
                if (turn.Confidence is double confidence)
                {
                    Assert.True(confidence >= 0.0 && confidence <= 1.0, $"Turn confidence ({confidence}) must be between 0.0 and 1.0.");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static float[] CreateSineWave(double durationSeconds, int sampleRate)
    {
        int sampleCount = (int)(durationSeconds * sampleRate);
        var samples = new float[sampleCount];
        for (int index = 0; index < sampleCount; index++)
        {
            double t = index / (double)sampleRate;
            samples[index] = (float)(Math.Sin(2d * Math.PI * 220d * t) * 0.2d);
        }

        return samples;
    }

    private static string GetLocalCacheModelPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trackdub",
            "model-cache",
            "cgus",
            "diar_streaming_sortformer_4spk-v2.1-onnx",
            "onnx",
            "model.onnx");

    private static async Task WriteTestWaveAsync(
        string path,
        double durationSeconds,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        int sampleCount = (int)(durationSeconds * sampleRate);
        short[] samples = new short[sampleCount];
        for (int index = 0; index < sampleCount; index++)
        {
            double t = index / (double)sampleRate;
            samples[index] = (short)(Math.Sin(2d * Math.PI * 220d * t) * short.MaxValue * 0.2d);
        }

        byte[] pcmData = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, pcmData, 0, pcmData.Length);
        byte[] header = new byte[44];
        "RIFF"u8.CopyTo(header.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 36 + pcmData.Length);
        "WAVE"u8.CopyTo(header.AsSpan(8, 4));
        "fmt "u8.CopyTo(header.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), sampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 16);
        "data"u8.CopyTo(header.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), pcmData.Length);

        await using FileStream stream = File.Create(path);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(pcmData, cancellationToken).ConfigureAwait(false);
    }

    private static BundledModelManifestRegistry LoadRegistry()
    {
        if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error) ||
            registry is null)
        {
            throw new InvalidOperationException(error ?? "Bundled model manifest was not found.");
        }

        return registry;
    }

    private static string? ResolveFixtureModelPath(BundledModelManifestRegistry registry)
    {
        if (!registry.TryResolve("sortformer-diarizer-4spk-v2.1", out BundledModelManifestResolution? resolution) ||
            resolution is null ||
            !Directory.Exists(resolution.Entry.RootDirectory) ||
            !File.Exists(resolution.Entry.DefaultBenchmarkEntryPath))
        {
            return null;
        }

        return resolution.Entry.DefaultBenchmarkEntryPath;
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class SortFormerFixtureFactAttribute : FactAttribute
    {
        public SortFormerFixtureFactAttribute()
        {
            if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out _) ||
                registry is null)
            {
                Skip = "Bundled model manifest was not found.";
                return;
            }

            try
            {
                if (ResolveFixtureModelPath(registry) is null)
                {
                    Skip = "SortFormer ONNX fixture is not present in the local models directory.";
                }
            }
            catch (Exception)
            {
                Skip = "SortFormer ONNX fixture is not present in the local models directory.";
            }
        }
    }

    private sealed class LocalSortFormerModelFactAttribute : FactAttribute
    {
        public LocalSortFormerModelFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("TRACKDUB_RUN_LOCAL_MODEL_TESTS"),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = "Set TRACKDUB_RUN_LOCAL_MODEL_TESTS=1 to run local cached model tests.";
                return;
            }

            if (!File.Exists(GetLocalCacheModelPath()))
            {
                Skip = "SortFormer ONNX model is not present in the local Trackdub model cache.";
            }
        }
    }

    private sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
    {
        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan);
    }
}
