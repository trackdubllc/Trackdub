using Trackdub.Composition.StarterPacks;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Optional GPU integration smoke for starter-pack turbo targets via the same
/// planner smoke path used by <c>trackdub providers trt-rtx smoke</c>.
/// </summary>
public sealed class TrtRtxStarterPackSmokeTests
{
    [RequiresTrtRtxFact]
    public async Task StarterPackTurboGpuModels_PassTrtRtxPlannerSmokeWhenCached()
    {
        TrtRtxStarterPackSmokeReport report = await TrtRtxStarterPackSmokeRunner
            .RunAsync(modelCacheDirectory: null, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.True(report.HasAttempts, "No TRT RTX smoke targets were cached locally. Download starter-pack models first.");
        Assert.False(report.HasFailures, string.Join(
            Environment.NewLine,
            report.Targets
                .Where(target => target.Status == TrtRtxStarterPackSmokeTargetStatus.Failed)
                .Select(target => $"{target.Label}: {target.Detail ?? "smoke test failed"}")));
    }
}
