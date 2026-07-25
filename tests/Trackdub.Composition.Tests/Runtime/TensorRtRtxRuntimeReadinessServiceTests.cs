using Trackdub.Composition.Runtime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Composition.Tests.Runtime;

public sealed class TensorRtRtxRuntimeReadinessServiceTests
{
    [Fact]
    public async Task ProbeAsync_EpDownloadFailed_AllowsRetryInstall()
    {
        var probe = new StubTensorRtRtxReadinessProbe(new TensorRtRtxReadinessReport(
            ProviderId: TensorRtRtxProviderIds.PluginEpAbi,
            Route: TensorRtRtxPlatformRoute.PluginEpAbi,
            Blocker: TensorRtRtxReadinessBlocker.EpDownloadFailed,
            IsHardwareEligible: true,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "TensorRT RTX EP bundle download did not complete."));
        var service = new TensorRtRtxRuntimeReadinessService(probe);

        TensorRtRtxRuntimeReadinessSnapshot snapshot = await service.ProbeAsync(
            allowProviderDownloads: false,
            CancellationToken.None);

        Assert.False(snapshot.IsReady);
        Assert.Equal("Download failed", snapshot.StatusLabel);
        Assert.True(snapshot.CanInstallWinMlProvider);
        Assert.NotNull(snapshot.InstallHint);
    }

    private sealed class StubTensorRtRtxReadinessProbe(TensorRtRtxReadinessReport report) : ITensorRtRtxReadinessProbe
    {
        public Task<TensorRtRtxReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken) =>
            Task.FromResult(report);
    }
}
