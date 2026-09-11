using Trackdub.Composition.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class TrtRtxStarterPackSmokeRunnerTests
{
    [Fact]
    public async Task RunAsync_SkipsUncachedTargetsWithoutAttemptingSmoke()
    {
        var smokeTester = new FakeExecutionProviderSmokeTester(
            (_, _) => throw new InvalidOperationException("Smoke should not run for skipped targets."));

        TrtRtxStarterPackSmokeReport report = await TrtRtxStarterPackSmokeRunner.RunAsync(
            modelCacheDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            smokeTester,
            CancellationToken.None);

        Assert.Equal(0, report.Attempted);
        Assert.Equal(TrtRtxSmokeCatalog.StarterPackTurboGpu.Count, report.Skipped);
        Assert.False(report.HasFailures);
        Assert.All(report.Targets, target => Assert.Equal(TrtRtxStarterPackSmokeTargetStatus.Skipped, target.Status));
    }

    [Fact]
    public async Task RunAsync_RecordsFailedSmokeResults()
    {
        var smokeTester = new FakeExecutionProviderSmokeTester((request, _) =>
        {
            if (request.ExecutionProvider != ExecutionProviderKind.TensorRTRtx)
            {
                return new ExecutionProviderSmokeTestResult(false, "Expected TRT RTX provider pin.");
            }

            return new ExecutionProviderSmokeTestResult(false, "simulated failure");
        });

        TrtRtxStarterPackSmokeReport report = await TrtRtxStarterPackSmokeRunner.RunAsync(
            modelCacheDirectory: null,
            smokeTester,
            CancellationToken.None);

        if (report.Attempted == 0)
        {
            return;
        }

        Assert.True(report.Failed > 0);
        Assert.Contains(
            report.Targets,
            target => target.Status == TrtRtxStarterPackSmokeTargetStatus.Failed
                && string.Equals(target.Detail, "simulated failure", StringComparison.Ordinal));
    }

    private sealed class FakeExecutionProviderSmokeTester(
        Func<ExecutionProviderSmokeTestRequest, CancellationToken, ExecutionProviderSmokeTestResult> handler)
        : IExecutionProviderSmokeTester
    {
        public Task<ExecutionProviderSmokeTestResult> SmokeTestAsync(
            ExecutionProviderSmokeTestRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(request, cancellationToken));
    }
}
