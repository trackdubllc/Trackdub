using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

public static class StageRuntimePlanExtensions
{
    /// <summary>
    /// Returns <c>true</c> when the plan is in a status that allows the engine to execute
    /// inference: either <see cref="StageRuntimePlanStatus.Ready"/> (CPU file-checked)
    /// or <see cref="StageRuntimePlanStatus.Verified"/> (non-CPU smoke-tested).
    /// </summary>
    public static bool IsRunnable(this StageRuntimePlan plan) =>
        plan.Status is StageRuntimePlanStatus.Ready or StageRuntimePlanStatus.Verified;

    /// <summary>
    /// Returns <c>true</c> when the plan status indicates the runtime is ready or verified.
    /// Equivalent to <see cref="IsRunnable(StageRuntimePlan)"/> but operates directly on the
    /// status enum for callsites that have only the value.
    /// </summary>
    public static bool IsRunnable(this StageRuntimePlanStatus status) =>
        status is StageRuntimePlanStatus.Ready or StageRuntimePlanStatus.Verified;
}
