using Trackdub.Composition.Runtime.Planning;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

public sealed class BundledInventoryRuntimePlannerTests
{
    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task RuntimePlanner_UsesBundledManifestInventoryForVadCpuPlan()
    {
        Assert.True(BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error), error);
        Assert.NotNull(registry);

        var planner = new RuntimePlanner(
            registry!,
            new MachineHardwareProfileProvider(),
            new OnnxExecutionProviderDiscovery(new NullOpenVinoAvailabilityProvider()),
            new PassingSmokeTester(),
            new BundledManifestModelCacheInventory(registry));

        StageRuntimePlan plan = await planner.PlanAsync(new StageRuntimePlanningRequest(RuntimeStage.Vad));

        Assert.True(plan.IsRunnable(), $"Expected runnable plan but got {plan.Status}");
        Assert.Equal("onnx-community/silero-vad", plan.ModelId);
        Assert.NotNull(plan.ExecutionProvider);
    }

    private sealed class PassingSmokeTester : IExecutionProviderSmokeTester
    {
        public Task<ExecutionProviderSmokeTestResult> SmokeTestAsync(
            ExecutionProviderSmokeTestRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionProviderSmokeTestResult(true));
    }
}
