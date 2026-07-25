using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

public interface IRuntimePlanner
{
    Task<StageRuntimePlan> PlanAsync(
        StageRuntimePlanningRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached runtime plans and cached hardware/provider state.
    /// Call after model files change on disk, after hardware topology changes
    /// (GPU hotplug, driver reinstall, display adapter enumeration reordering),
    /// or after execution provider availability changes.
    /// </summary>
    void InvalidatePlanCache() { }
}

public interface IHardwareProfileProvider
{
    Task<HardwareProfile> GetCurrentAsync(CancellationToken cancellationToken = default);
}

public interface IExecutionProviderDiscovery
{
    Task<IReadOnlyList<ExecutionProviderAvailability>> DiscoverAsync(
        HardwareProfile hardwareProfile,
        CancellationToken cancellationToken = default);
}

public interface IExecutionProviderSmokeTester
{
    Task<ExecutionProviderSmokeTestResult> SmokeTestAsync(
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken = default);
}

public interface IModelCacheInventory
{
    Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default);
}
