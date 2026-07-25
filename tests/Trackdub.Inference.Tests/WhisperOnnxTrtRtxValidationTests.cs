using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Whisper;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Hardware validation tests for TRT-RTX optimization of whisper-onnx models.
///
/// Prerequisites before removing [Fact(Skip = ...)] from any test here:
///   1. Download the whisper-onnx model:
///      dotnet run --project src/Trackdub.Tools -- ingest --model onnx-community/whisper-{size}
///   2. Run Olive TRT-RTX optimization and staging:
///      .\tools\olive\Validate-WhisperOnnxTrtRtx.ps1 -ModelSize {size}
///   3. Verify build/whisper-{size}-onnx-trtrtx-validated/ was created.
///   4. Remove the Skip attribute, run:
///      dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~WhisperOnnxTrtRtx"
///   5. If tests pass, apply manifest+test flip:
///      .\tools\olive\Flip-WhisperOnnxTrtRtx.ps1
///
/// These tests never run in CI (either guarded by Skip or by staging dir absence).
/// </summary>
public sealed class WhisperOnnxTrtRtxValidationTests
{
    [Fact(Skip = "Pending TRT-RTX validation — run tools/olive/Validate-WhisperOnnxTrtRtx.ps1 -ModelSize tiny, then remove this Skip")]
    public async Task WhisperOnnxTrtRtx_TinyModel_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("tiny", "onnx-community/whisper-tiny");
    }

    [Fact(Skip = "Pending TRT-RTX validation — run tools/olive/Validate-WhisperOnnxTrtRtx.ps1 -ModelSize base, then remove this Skip")]
    public async Task WhisperOnnxTrtRtx_BaseModel_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("base", "onnx-community/whisper-base");
    }

    [Fact(Skip = "Pending TRT-RTX validation — run tools/olive/Validate-WhisperOnnxTrtRtx.ps1 -ModelSize small, then remove this Skip")]
    public async Task WhisperOnnxTrtRtx_SmallModel_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("small", "onnx-community/whisper-small");
    }

    [Fact(Skip = "Pending TRT-RTX validation — run tools/olive/Validate-WhisperOnnxTrtRtx.ps1 -ModelSize medium, then remove this Skip")]
    public async Task WhisperOnnxTrtRtx_MediumModel_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("medium", "Xenova/whisper-medium");
    }

    [Fact(Skip = "Pending TRT-RTX validation — run tools/olive/Validate-WhisperOnnxTrtRtx.ps1 -ModelSize large-v3, then remove this Skip")]
    public async Task WhisperOnnxTrtRtx_LargeV3Model_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("large-v3", "Xenova/whisper-large-v3");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared implementation
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task RunTrtRtxSilenceSmokeAsync(string modelSize, string modelId)
    {
        string stagingDir = Path.Combine(FindRepoRoot(), "build", $"whisper-{modelSize}-onnx-trtrtx-validated");

        Assert.True(
            Directory.Exists(stagingDir),
            $"Staging directory not found: {stagingDir}\n" +
            $"Run: .\\tools\\olive\\Validate-WhisperOnnxTrtRtx.ps1 -ModelSize {modelSize}");

        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            // No manifest registry needed: Discover() falls through to directory scan when given
            // an absolute path, matching the staging layout the validate script produces.
            var resolver = new BenchmarkModelPathResolver();

            var engine = new WhisperOnnxAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = modelId,
                    ModelAlias = stagingDir,
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.TensorRTRtx
                }),
                resolver);

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [new SpeechRegion(0, 0.0, 0.8)],
                CancellationToken.None);

            RecognizedTranscriptSegment segment = Assert.Single(segments);
            Assert.Equal(0, segment.Index);
            Assert.NotNull(engine.LastExecutionSummary);

            // Confirm TRT-RTX (or its DirectML fallback on non-NVIDIA hardware) was selected.
            Assert.False(
                string.IsNullOrWhiteSpace(engine.LastExecutionSummary!.SelectedProvider),
                "SelectedProvider must not be empty — session provider resolution failed.");
        }
        finally
        {
            if (File.Exists(wavePath))
                File.Delete(wavePath);
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Trackdub.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string CreateSilenceWaveFile(double durationSeconds)
    {
        const int sampleRate = 48000;
        const int channels = 1;
        const int bitsPerSample = 16;
        int numSamples = (int)(sampleRate * durationSeconds);
        int dataSize = numSamples * channels * (bitsPerSample / 8);

        string path = Path.Combine(Path.GetTempPath(), $"silence_{Guid.NewGuid():N}.wav");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write((short)bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);

        return path;
    }
}

file sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
{
    public Task<StageRuntimePlan> PlanAsync(StageRuntimePlanningRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(plan);
}
