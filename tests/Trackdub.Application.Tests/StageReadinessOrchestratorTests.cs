using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Tests;

public sealed class StageReadinessOrchestratorTests
{
    [Fact]
    public async Task ProvisionStageAsync_UnknownStage_ReturnsNotReady()
    {
        var orchestrator = new StageReadinessOrchestrator(new RuntimeModelSetupCoordinator());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());
        var request = new StageReadinessProvisionRequest(
            StageKey: "not-a-real-stage",
            Workspace: null!,
            Selections: selections,
            Callbacks: new RuntimeModelSetupCallbacks(
                _ => Task.FromResult(RuntimeModelSetupDecision.Cancel),
                () => Task.FromResult<string?>(null),
                _ => new Progress<ModelDownloadProgress>(),
                (_, _) => Task.CompletedTask),
            SourceLanguageCode: null,
            TargetLanguageCode: null,
            LipSyncModelAlias: null,
            LipSynthesisModelAlias: null,
            RequiresVoiceClone: false);

        RuntimeModelSetupResult result = await orchestrator.ProvisionStageAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsReady);
        Assert.Empty(result.SkippedStages);
    }
}
