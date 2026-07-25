using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackPresentationServiceTests
{
    [Fact]
    public async Task GetRecommendedPackIdAsync_falls_back_to_tier_pack_when_no_runnable_packs()
    {
        StarterPackPresentationService service = CreateService();

        string? recommended = await service.GetRecommendedPackIdAsync();

        Assert.Equal("balanced", recommended);
    }

    private static StarterPackPresentationService CreateService()
    {
        Assert.True(
            BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error),
            error);

        StarterPackCatalog catalog = new();
        var inventory = new FakeModelInventoryService();
        var settings = new FakeStudioSettingsService();
        var consent = new FakeConsentService();
        var hardwareProfiler = new FakeHardwareProfilerService { EffectiveModelTier = "balanced" };
        var runtimePlanner = new FakeRuntimePlanner
        {
            PlanHandler = request => new StageRuntimePlan
            {
                Stage = request.Stage,
                Status = StageRuntimePlanStatus.DownloadRequired,
                Variant = request.PreferredModelVariantAlias,
                ExecutionProvider = request.PreferredExecutionProvider,
                Fallback = new RuntimePlanFallback(RuntimePlanFallbackCode.ModelNotCached, "cache miss")
            }
        };
        var compatibility = new StarterPackCompatibilityService(
            catalog,
            hardwareProfiler,
            runtimePlanner);

        var cloudCredentialReadiness = new FakeCloudCredentialReadinessService(ready: true);
        var hardwareProfileProvider = new Trackdub.Inference.Onnx.Runtime.Planning.MachineHardwareProfileProvider();
        var gpuSetupAdvisor = new StarterPackGpuSetupAdvisor(hardwareProfileProvider);

        return new StarterPackPresentationService(
            catalog,
            inventory,
            settings,
            consent,
            hardwareProfiler,
            compatibility,
            cloudCredentialReadiness,
            registry!,
            gpuSetupAdvisor);
    }

    private sealed class FakeCloudCredentialReadinessService(bool ready) : ICloudCredentialReadiness
    {
        public Task<CloudCredentialReadinessReport> EvaluateAsync(
            StarterPackCloudDefaults cloudDefaults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ready
                ? new CloudCredentialReadinessReport(true, [], null)
                : new CloudCredentialReadinessReport(
                    false,
                    ["openai"],
                    "Configure OpenAI API keys in Cloud Models before applying this pack."));
    }

}
