using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Routing;

internal static class InferenceEngineAdapterSelector
{
    public static TAdapter SelectForPlan<TAdapter>(
        RuntimeStage stage,
        StageRuntimePlan plan,
        IEnumerable<TAdapter> adapters)
        where TAdapter : IInferenceEngineAdapter
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(adapters);

        TAdapter[] adapterList = adapters.ToArray();
        if (adapterList.Length == 0)
        {
            throw new InvalidOperationException($"No {stage} inference adapters are registered.");
        }

        if (!plan.IsRunnable())
        {
            string? rawDetail = plan.Fallback?.Detail;
            string? fallbackDetail = string.IsNullOrWhiteSpace(rawDetail) ? null : rawDetail;
            string modelLabel = plan.ModelAlias ?? plan.ModelId ?? "unknown";
            string message = plan.Status switch
            {
                StageRuntimePlanStatus.DownloadRequired =>
                    fallbackDetail is null
                        ? $"Model setup required before running {stage} ({modelLabel})."
                        : $"Model setup required before running {stage} ({modelLabel}): {fallbackDetail}",
                _ => fallbackDetail ?? $"Runtime planner did not produce a selectable {stage} plan."
            };
            throw new InvalidOperationException(message);
        }

        string engineFamily = NormalizeEngineFamily(plan.EngineFamily) ??
            throw new InvalidOperationException($"Runtime planner did not identify an engine family for {stage}.");

        TAdapter? adapter = adapterList.FirstOrDefault(candidate =>
            string.Equals(candidate.EngineFamily, engineFamily, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            throw new InvalidOperationException(
                $"No {stage} inference adapter is registered for engine family '{engineFamily}'.");
        }

        return adapter;
    }

    public static string? NormalizeEngineFamily(string? engineFamily) =>
        string.IsNullOrWhiteSpace(engineFamily)
            ? null
            : engineFamily.Trim();
}
