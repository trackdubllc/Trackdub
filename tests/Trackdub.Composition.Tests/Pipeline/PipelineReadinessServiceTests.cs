using Trackdub.Application.Transcripts;
using Trackdub.Composition.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.Pipeline;

public sealed class PipelineReadinessServiceTests
{
    [Fact]
    public async Task EvaluateAsync_normalizes_source_language_for_translation_planning()
    {
        string? capturedSourceLanguage = null;
        string? capturedTargetLanguage = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedSourceLanguage = req.SourceLanguage;
                capturedTargetLanguage = req.TargetLanguage;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TranslationModelAlias: "opus-mt-en-es");

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Translation],
            selections,
            state: null,
            sourceLanguageCode: "pt-BR",
            targetLanguageCode: "es-MX");

        Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Ready, report.Stages[0].Status);
        Assert.Equal("pt", capturedSourceLanguage);
        Assert.Equal("es", capturedTargetLanguage);
    }

    [Fact]
    public async Task EvaluateAsync_uses_planner_for_lip_sync_instead_of_hardcoded_skip()
    {
        int planCalls = 0;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                Assert.Equal(RuntimeStage.LipSync, req.Stage);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.LipSync],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>()),
            state: null);

        Assert.Equal(1, planCalls);
        StageReadiness lipSync = Assert.Single(report.Stages);
        Assert.Equal(StageNames.LipSync, lipSync.StageName);
        Assert.Equal(ReadinessState.Ready, lipSync.Status);
    }

    [Fact]
    public async Task EvaluateAsync_passes_lip_sync_alias_from_selections_to_planner()
    {
        string? capturedAlias = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedAlias = req.PreferredModelAlias;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.LipSync],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
                LipSyncModelAlias: "latentsync-1.6"),
            state: null);

        Assert.Equal("latentsync-1.6", capturedAlias);
        StageReadiness lipSync = Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Ready, lipSync.Status);
    }

    [Fact]
    public async Task EvaluateAsync_passes_lip_synthesis_alias_from_selections_to_planner()
    {
        string? capturedAlias = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedAlias = req.PreferredModelAlias;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.LipSynthesis],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
                LipSynthesisModelAlias: "latentsync-1.6"),
            state: null);

        Assert.Equal("latentsync-1.6", capturedAlias);
        StageReadiness lipSynthesis = Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Ready, lipSynthesis.Status);
    }

    private sealed class NullCloudApiKeyProvider : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }
}
