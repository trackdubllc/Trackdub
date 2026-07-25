using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Planning;

public static class StageRuntimePlanningRequestFactory
{
    public static async Task<StageRuntimePlanningRequest> ApplyPreferredModelTierAsync(
        StageRuntimePlanningRequest request,
        IRuntimePlanningPreferences? preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (preferences is null)
        {
            return request;
        }

        string? tier = await preferences
            .GetPreferredModelTierAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tier))
        {
            return request;
        }

        return request with { PreferredModelTier = tier };
    }
}
