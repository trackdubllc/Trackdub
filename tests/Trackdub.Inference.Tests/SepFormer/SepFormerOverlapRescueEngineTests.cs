using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.SepFormer;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests.SepFormer;

public sealed class SepFormerOverlapRescueEngineTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"sepformer-overlap-tests-{Guid.NewGuid():N}");

    public SepFormerOverlapRescueEngineTests() => Directory.CreateDirectory(tempDir);

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task RescueAsync_WritesSourceCandidatePaths()
    {
        string regionPath = Path.Combine(tempDir, "region.wav");
        string candidate0Path = Path.Combine(tempDir, "candidate-0.wav");
        string candidate1Path = Path.Combine(tempDir, "candidate-1.wav");

        await WriteSilenceWavAsync(regionPath, sampleRate: 16000, durationSamples: 800);

        float[] source0 = new float[800];
        float[] source1 = new float[800];

        var fakeSeparator = new CapturingSepFormerSeparator(
            new SepFormerSeparation(source0, source1, 16000, ChunkCount: 1));

        var plan = new StageRuntimePlan
        {
            Stage = RuntimeStage.OverlapRescue,
            Status = StageRuntimePlanStatus.Ready,
            EngineFamily = SepFormerOverlapRescueEngine.EngineFamilyName,
            ModelId = "tonythethompson/sepformer-whamr16k-onnx",
            ModelAlias = "sepformer",
            ModelEntryPath = Path.Combine(tempDir, "sepformer.onnx"),
            ExecutionProvider = ExecutionProviderKind.Cpu
        };

        File.WriteAllText(plan.ModelEntryPath, "stub");

        var engine = new SepFormerOverlapRescueEngine(fakeSeparator);
        var request = new OverlapRescueRequest(
            regionPath,
            candidate0Path,
            candidate1Path,
            RegionStartSeconds: 1.0,
            RegionEndSeconds: 2.0,
            PreferredModelAlias: "sepformer");

        OverlapRescueResult result = await engine.RescueAsync(request, plan, progress: null, CancellationToken.None);

        Assert.True(File.Exists(candidate0Path));
        Assert.True(File.Exists(candidate1Path));
        Assert.Equal(16000, result.SampleRate);
        Assert.NotNull(engine.LastExecutionSummary);
    }

    [Fact]
    public async Task RescueAsync_ThrowsWhenPlanNotReady()
    {
        string regionPath = Path.Combine(tempDir, "region-blocked.wav");
        await WriteSilenceWavAsync(regionPath, sampleRate: 16000, durationSamples: 160);

        var plan = new StageRuntimePlan
        {
            Stage = RuntimeStage.OverlapRescue,
            Status = StageRuntimePlanStatus.Blocked,
            EngineFamily = SepFormerOverlapRescueEngine.EngineFamilyName
        };

        var engine = new SepFormerOverlapRescueEngine(new CapturingSepFormerSeparator(
            new SepFormerSeparation([], [], 16000, 0)));

        var request = new OverlapRescueRequest(
            regionPath,
            Path.Combine(tempDir, "c0.wav"),
            Path.Combine(tempDir, "c1.wav"),
            RegionStartSeconds: 0,
            RegionEndSeconds: 0.5);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RescueAsync(request, plan, progress: null, CancellationToken.None));

        Assert.Contains("not ready", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteSilenceWavAsync(string path, int sampleRate, int durationSamples)
    {
        float[] silence = new float[durationSamples];
        await WaveAudioWriter.WriteMonoPcm16Async(path, silence, sampleRate, CancellationToken.None);
    }

    private sealed class CapturingSepFormerSeparator(SepFormerSeparation? fixedResult) : ISepFormerSeparator
    {
        public SepFormerRegionRequest? LastRegionRequest { get; private set; }

        public Task<SepFormerSeparation> SeparateAsync(
            SepFormerSeparatorRequest request,
            IProgress<StemSeparationProgress>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(fixedResult ?? new SepFormerSeparation(
                new float[request.Samples.Length],
                new float[request.Samples.Length],
                request.SampleRate,
                1));

        public Task<SepFormerSeparation> SeparateRegionAsync(
            SepFormerRegionRequest request,
            CancellationToken cancellationToken)
        {
            LastRegionRequest = request;
            SepFormerSeparation result = fixedResult ?? new SepFormerSeparation(
                new float[request.Samples.Length],
                new float[request.Samples.Length],
                request.SampleRate,
                1);

            return Task.FromResult(result);
        }
    }
}
