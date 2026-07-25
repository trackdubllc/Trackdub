using Trackdub.Contracts.Licensing;
using Trackdub.Application.Pipeline;
using Trackdub.Composition.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.Pipeline;

public sealed class PipelinePreFlightCheckerTests
{
    // ------------------------------------------------------------------ blocked plan → throws

    [Theory]
    [InlineData(StageNames.Vad, RuntimeStage.Vad)]
    [InlineData(StageNames.Asr, RuntimeStage.Asr)]
    [InlineData(StageNames.Diarization, RuntimeStage.Diarization)]
    [InlineData(StageNames.Translation, RuntimeStage.Translation)]
    [InlineData(StageNames.Tts, RuntimeStage.Tts)]
    [InlineData(StageNames.Separation, RuntimeStage.Separation)]
    public async Task EnsureModelsAvailableAsync_throws_when_plan_status_is_blocked(
        string stageName,
        RuntimeStage expectedStage)
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = _ => BlockedPlan(expectedStage,
                new RuntimePlanFallback(RuntimePlanFallbackCode.NoCompatibleVariant, "No model found"))
        };
        IPipelinePreFlightChecker checker = new RuntimePlannerPreFlightChecker(planner);

        await Assert.ThrowsAsync<RequiredModelNotAvailableException>(
            () => checker.EnsureModelsAvailableAsync(stageName, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureModelsAvailableAsync_throws_with_message_containing_stage_name()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = _ => BlockedPlan(RuntimeStage.Asr,
                new RuntimePlanFallback(RuntimePlanFallbackCode.ModelNotCached, "Missing onnx file"))
        };
        IPipelinePreFlightChecker checker = new RuntimePlannerPreFlightChecker(planner);

        RequiredModelNotAvailableException ex = await Assert.ThrowsAsync<RequiredModelNotAvailableException>(
            () => checker.EnsureModelsAvailableAsync(StageNames.Asr, TestContext.Current.CancellationToken));

        Assert.Contains(StageNames.Asr, ex.ModelId, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ ready → no throw

    [Theory]
    [InlineData(StageNames.Vad, RuntimeStage.Vad)]
    [InlineData(StageNames.Asr, RuntimeStage.Asr)]
    [InlineData(StageNames.Diarization, RuntimeStage.Diarization)]
    public async Task EnsureModelsAvailableAsync_does_not_throw_when_plan_is_ready(
        string stageName,
        RuntimeStage expectedStage)
    {
        int callCount = 0;
        RuntimeStage? calledWithStage = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                callCount++;
                calledWithStage = req.Stage;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };
        IPipelinePreFlightChecker checker = new RuntimePlannerPreFlightChecker(planner);

        await checker.EnsureModelsAvailableAsync(stageName, TestContext.Current.CancellationToken);

        Assert.Equal(1, callCount);
        Assert.Equal(expectedStage, calledWithStage);
    }

    [Theory]
    [InlineData(StageNames.Vad, RuntimeStage.Vad)]
    [InlineData(StageNames.Asr, RuntimeStage.Asr)]
    public async Task EnsureModelsAvailableAsync_throws_when_plan_status_is_download_required(
        string stageName,
        RuntimeStage expectedStage)
    {
        RuntimeStage? calledWithStage = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                calledWithStage = req.Stage;
                return new StageRuntimePlan
                {
                    Stage = req.Stage,
                    Status = StageRuntimePlanStatus.DownloadRequired,
                    ModelId = "test/model"
                };
            }
        };
        IPipelinePreFlightChecker checker = new RuntimePlannerPreFlightChecker(planner);

        RequiredModelNotAvailableException ex = await Assert.ThrowsAsync<RequiredModelNotAvailableException>(
            () => checker.EnsureModelsAvailableAsync(stageName, TestContext.Current.CancellationToken));

        Assert.True(ex.CanAutoDownload);
        Assert.Equal(expectedStage, calledWithStage);
    }

    // ------------------------------------------------------------------ non-planning stages → planner not called

    [Theory]
    [InlineData(StageNames.Export)]
    [InlineData(StageNames.SpeechEnhancement)]
    [InlineData(StageNames.AudioPreparation)]
    [InlineData(StageNames.PreviewMix)]
    [InlineData("unknown-stage")]
    public async Task EnsureModelsAvailableAsync_skips_planner_for_non_planning_stages(
        string stageName)
    {
        bool plannerWasCalled = false;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = _ =>
            {
                plannerWasCalled = true;
                throw new InvalidOperationException("Planner must not be called for non-planning stages.");
            }
        };
        IPipelinePreFlightChecker checker = new RuntimePlannerPreFlightChecker(planner);

        await checker.EnsureModelsAvailableAsync(stageName, TestContext.Current.CancellationToken);

        Assert.False(plannerWasCalled);
    }

    [Fact]
    public async Task EnsureModelsAvailableAsync_passes_source_language_for_asr_planning()
    {
        string? capturedSourceLanguage = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedSourceLanguage = req.SourceLanguage;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };
        IPipelinePreFlightChecker checker = new RuntimePlannerPreFlightChecker(planner);

        await checker.EnsureModelsAvailableAsync(
            StageNames.Asr,
            TestContext.Current.CancellationToken,
            sourceLanguageCode: "pt-BR");

        Assert.Equal("pt-BR", capturedSourceLanguage);
    }

    // ------------------------------------------------------------------ helpers

    private static StageRuntimePlan BlockedPlan(
        RuntimeStage stage,
        RuntimePlanFallback? fallback = null) =>
        new()
        {
            Stage = stage,
            Status = StageRuntimePlanStatus.Blocked,
            Fallback = fallback
        };
}
