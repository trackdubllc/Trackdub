using Trackdub.Composition.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

/// <summary>
/// ORT GenAI's NvTensorRtRtx device terminates the host process (native stack overflow)
/// on bundled GenAI models such as qwen-instruct (Qwen2.5-1.5B). A catchable smoke
/// failure cannot gate a fatal crash, so GenAI-loaded engine families must never be
/// offered TensorRT providers, and the smoke tester must refuse before touching
/// native code.
/// </summary>
public sealed class GenAiTensorRtExclusionTests
{
    public static IEnumerable<object[]> GenAiFamilyStages
    {
        get
        {
            yield return [RuntimeStage.Asr, "whisper-genai"];
            yield return [RuntimeStage.Translation, "phi-genai"];
            yield return [RuntimeStage.TextRefinement, "qwen-instruct"];
            yield return [RuntimeStage.TextRefinement, "phi-genai"];
        }
    }

    [Theory]
    [MemberData(nameof(GenAiFamilyStages))]
    public void GenAiEngineFamily_StageOverride_ExcludesTensorRtProviders(
        RuntimeStage stage,
        string engineFamily)
    {
        StageRuntimeRequirements requirements = StageRuntimeRequirementsCatalog.All[stage];

        Assert.NotNull(requirements.AllowedProvidersByEngineFamily);
        Assert.True(
            requirements.AllowedProvidersByEngineFamily.TryGetValue(
                engineFamily, out IReadOnlyList<ExecutionProviderKind>? allowed),
            $"Stage {stage} has no provider override for engine family '{engineFamily}'.");
        Assert.NotNull(allowed);
        Assert.DoesNotContain(ExecutionProviderKind.TensorRTRtx, allowed);
        Assert.DoesNotContain(ExecutionProviderKind.TensorRt, allowed);
    }

    [Theory]
    [InlineData(RuntimeStage.Asr, "whisper-genai")]
    [InlineData(RuntimeStage.Translation, "phi-genai")]
    [InlineData(RuntimeStage.TextRefinement, "qwen-instruct")]
    public async Task SmokeTest_GenAiFamilyOnTensorRtRtx_FailsWithoutTouchingNativeCode(
        RuntimeStage stage,
        string engineFamily)
    {
        var request = new ExecutionProviderSmokeTestRequest(
            stage,
            "model-id",
            "alias",
            engineFamily,
            "default",
            ExecutionProviderKind.TensorRTRtx,
            ModelRootPath: "does-not-exist",
            EntryPath: "does-not-exist/model.onnx");

        ExecutionProviderSmokeTestResult result = await new OnnxExecutionProviderSmokeTester()
            .SmokeTestAsync(request, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("NvTensorRtRtx", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("opus-mt")]
    [InlineData("madlad")]
    public async Task SmokeTest_FatalEncoderDecoderFamilyOnTensorRtRtx_FailsWithoutTouchingNativeCode(
        string engineFamily)
    {
        // The smoke sweep bypasses stage allow-lists, so the guard at the translation
        // smoke entry is what prevents the documented InferenceSession ctor stack
        // overflow from killing the host process.
        var request = new ExecutionProviderSmokeTestRequest(
            RuntimeStage.Translation,
            "model-id",
            "alias",
            engineFamily,
            "merged-decoder",
            ExecutionProviderKind.TensorRTRtx,
            ModelRootPath: "does-not-exist",
            EntryPath: "does-not-exist/model.onnx");

        ExecutionProviderSmokeTestResult result = await new OnnxExecutionProviderSmokeTester()
            .SmokeTestAsync(request, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("terminates the host process", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RuntimeStage.Translation, "opus-mt")]
    [InlineData(RuntimeStage.Translation, "madlad")]
    public void FatalEncoderDecoderFamily_StageOverride_ExcludesTensorRtProviders(
        RuntimeStage stage,
        string engineFamily)
    {
        StageRuntimeRequirements requirements = StageRuntimeRequirementsCatalog.All[stage];

        Assert.True(
            requirements.AllowedProvidersByEngineFamily!.TryGetValue(
                engineFamily, out IReadOnlyList<ExecutionProviderKind>? allowed));
        Assert.DoesNotContain(ExecutionProviderKind.TensorRTRtx, allowed);
        Assert.DoesNotContain(ExecutionProviderKind.TensorRt, allowed);
    }

    [Fact]
    public void VadStage_AllowsTensorRtRtx()
    {
        // VAD previously passed a TRT RTX smoke run; the per-model smoke gate decides.
        Assert.Contains(
            ExecutionProviderKind.TensorRTRtx,
            StageRuntimeRequirementsCatalog.All[RuntimeStage.Vad].AllowedProvidersThisMilestone);
    }

    [Fact]
    public async Task SmokeRunner_ReportsPerTargetProgress_ForSkippedTargets()
    {
        // A missing model must produce an incremental Skipped result even though the
        // aggregate report is only returned at the end.
        var targets = new[]
        {
            new TrtRtxSmokeCatalog.Target("not-a-real/model-ref", null, "missing-model"),
        };
        var reported = new List<TrtRtxStarterPackSmokeTargetResult>();

        TrtRtxStarterPackSmokeReport report = await TrtRtxStarterPackSmokeRunner.RunAsync(
            modelCacheDirectory: null,
            targets,
            new Progress<TrtRtxStarterPackSmokeTargetResult>(reported.Add),
            CancellationToken.None);

        TrtRtxStarterPackSmokeTargetResult result = Assert.Single(report.Targets);
        Assert.Equal(TrtRtxStarterPackSmokeTargetStatus.Skipped, result.Status);
        Assert.Single(reported);
        Assert.Equal("missing-model", reported[0].Label);
    }
}
