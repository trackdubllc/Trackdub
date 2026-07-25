using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.TestDoubles;

public sealed class FakeHardwareProfileProvider : IHardwareProfileProvider
{
    public HardwareProfile Profile { get; set; } = new HardwareProfile("windows", "x64", HasGpu: true, GpuDescription: "Test GPU");

    public Task<HardwareProfile> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Profile);
}

public sealed class FakeExecutionProviderDiscovery : IExecutionProviderDiscovery
{
    public IReadOnlyList<ExecutionProviderAvailability> Availabilities { get; set; } = [];

    public FakeExecutionProviderDiscovery() { }

    public FakeExecutionProviderDiscovery(IReadOnlyList<ExecutionProviderAvailability> availabilities)
    {
        Availabilities = availabilities;
    }

    public Task<IReadOnlyList<ExecutionProviderAvailability>> DiscoverAsync(
        HardwareProfile hardwareProfile,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Availabilities);
}

public sealed class FakeExecutionProviderSmokeTester(
    Func<ExecutionProviderSmokeTestRequest, ExecutionProviderSmokeTestResult>? handler = null)
    : IExecutionProviderSmokeTester
{
    public Task<ExecutionProviderSmokeTestResult> SmokeTestAsync(
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(handler?.Invoke(request) ?? new ExecutionProviderSmokeTestResult(true));
    }
}

public sealed class InMemoryModelCacheInventory(IReadOnlyList<LocalModelCacheRecord> records) : IModelCacheInventory
{
    public Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(records);
}

public sealed class FakeRuntimePlanner : IRuntimePlanner
{
    public Func<StageRuntimePlanningRequest, StageRuntimePlan>? PlanHandler { get; set; }

    public Task<StageRuntimePlan> PlanAsync(
        StageRuntimePlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(PlanHandler?.Invoke(request) ?? new StageRuntimePlan { Stage = request.Stage, Status = StageRuntimePlanStatus.Ready });
    }
}

public sealed class FakeRuntimeSelectionService : Trackdub.Application.Runtime.IRuntimeSelectionService
{
    public Func<RuntimeStage, ExecutionProviderKind?, RuntimeRoute>? SelectRouteHandler { get; set; }
    public List<ProviderCapability> Capabilities { get; set; } = [];

    public Task<RuntimeRoute> SelectRouteAsync(
        RuntimeStage stage,
        ExecutionProviderKind? preference = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SelectRouteHandler?.Invoke(stage, preference) ?? new RuntimeRoute { Stage = stage, SelectedProvider = preference ?? ExecutionProviderKind.Cpu });
    }

    public Task<IReadOnlyList<ProviderCapability>> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ProviderCapability>>(Capabilities);
    }
}
