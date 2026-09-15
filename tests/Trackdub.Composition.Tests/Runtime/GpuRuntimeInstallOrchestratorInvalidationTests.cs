using Trackdub.Application.Runtime;
using Trackdub.Composition.Runtime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Inference.Onnx.Runtime.Planning;

namespace Trackdub.Composition.Tests.Runtime;

/// <summary>
/// Guards the FEAT-003 fix: the parallel install path
/// (<see cref="GpuRuntimeInstallOrchestrator"/>) performs the same category of state-changing
/// installs as the CLI handler but previously never invalidated the shared caching readiness
/// probes, so a long-lived process kept serving stale readiness after an orchestrator install.
/// After a successful install the orchestrator must invalidate the corresponding
/// <see cref="IReadinessProbeCache"/> so a later re-probe observes the freshly installed state.
/// </summary>
/// <remarks>
/// <see cref="GpuRuntimeInstallOrchestrator.InstallAsync"/> is guarded to run only on Windows, so
/// on non-Windows hosts these tests early-return (matching the repo convention for Windows-only
/// orchestration code). On Windows CI they exercise the full install path and fail if the
/// invalidation is removed.
/// </remarks>
public sealed class GpuRuntimeInstallOrchestratorInvalidationTests
{
    [Fact]
    public async Task InstallTensorRtRtx_InvalidatesReadinessCache_SoLaterProbeSeesInstalledState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        TensorRtRtxReadinessReport preInstall = NotPresentReport();
        TensorRtRtxReadinessReport postInstall = ReadyReport();
        TensorRtRtxReadinessReport current = preInstall;

        var counting = new CountingTensorRtRtxReadinessProbe(() => current);
        var cachedProbe = new CachingTensorRtRtxReadinessProbe(counting);

        // Prime the cache: a long-lived process (Studio) that already probed readiness once.
        TensorRtRtxReadinessReport primed =
            await cachedProbe.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);
        Assert.Same(preInstall, primed);
        Assert.Equal(1, counting.CallCount);

        // A successful install flips the underlying state to ready.
        var installer = new FakeTrtRtxEpInstaller(succeeded: true, onInstall: () => current = postInstall);

        var orchestrator = new GpuRuntimeInstallOrchestrator(
            trtRtxEpInstaller: installer,
            tensorRtRtxReadinessProbe: cachedProbe);

        GpuRuntimeInstallResult result = await orchestrator.InstallAsync(
            StarterPackGpuRuntimeKind.NvidiaTensorRtRtx,
            new Progress<string>(),
            CancellationToken.None);

        Assert.True(result.Succeeded);

        // Because the orchestrator invalidated the cache after the successful install, this
        // re-probe must re-run the underlying probe and observe the post-install ready state
        // rather than the stale primed snapshot. If the invalidation is removed, the cached
        // pre-install report is returned and CallCount stays at 1, failing these assertions.
        TensorRtRtxReadinessReport after =
            await cachedProbe.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);

        Assert.Same(postInstall, after);
        Assert.True(after.IsReady);
        Assert.Equal(2, counting.CallCount);
    }

    [Fact]
    public async Task InstallTensorRtRtx_FailedInstall_DoesNotInvalidateCache()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        TensorRtRtxReadinessReport preInstall = NotPresentReport();
        TensorRtRtxReadinessReport current = preInstall;

        var counting = new CountingTensorRtRtxReadinessProbe(() => current);
        var cachedProbe = new CachingTensorRtRtxReadinessProbe(counting);

        // Prime the cache: a long-lived process (Studio) that already probed readiness once.
        TensorRtRtxReadinessReport primed =
            await cachedProbe.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);
        Assert.Same(preInstall, primed);
        Assert.Equal(1, counting.CallCount);

        // A failed install does not change state (the onInstall callback is never invoked).
        var installer = new FakeTrtRtxEpInstaller(succeeded: false, onInstall: () => current = ReadyReport());

        var orchestrator = new GpuRuntimeInstallOrchestrator(
            trtRtxEpInstaller: installer,
            tensorRtRtxReadinessProbe: cachedProbe);

        GpuRuntimeInstallResult result = await orchestrator.InstallAsync(
            StarterPackGpuRuntimeKind.NvidiaTensorRtRtx,
            new Progress<string>(),
            CancellationToken.None);

        Assert.False(result.Succeeded);

        // Because the install failed, the orchestrator must NOT invalidate the cache. A later
        // re-probe should return the same cached pre-install snapshot without re-running the
        // underlying probe. If the guard is removed and the cache is incorrectly invalidated on
        // failure, CallCount would increment to 2, failing this assertion.
        TensorRtRtxReadinessReport after =
            await cachedProbe.ProbeAsync(allowProviderDownloads: false, CancellationToken.None);

        Assert.Same(preInstall, after);
        Assert.False(after.IsReady);
        // The probe was never re-run; the cached result was returned.
        Assert.Equal(1, counting.CallCount);
    }

    private static TensorRtRtxReadinessReport NotPresentReport() =>
        new(
            ProviderId: TensorRtRtxProviderIds.PluginEpAbi,
            Route: TensorRtRtxPlatformRoute.PluginEpAbi,
            Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
            IsHardwareEligible: true,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "TensorRT RTX EP plugin is not installed.");

    private static TensorRtRtxReadinessReport ReadyReport() =>
        new(
            ProviderId: TensorRtRtxProviderIds.PluginEpAbi,
            Route: TensorRtRtxPlatformRoute.PluginEpAbi,
            Blocker: TensorRtRtxReadinessBlocker.None,
            IsHardwareEligible: true,
            IsOrtProviderListed: true,
            IsRegisteredWithOrt: true,
            Detail: "TensorRT RTX EP plugin installed and registered.");

    private sealed class FakeTrtRtxEpInstaller(bool succeeded, Action onInstall) : ITrtRtxEpInstaller
    {
        public Task<TrtRtxEpInstallResult> EnsureInstalledAsync(
            IProgress<string> progress,
            CancellationToken cancellationToken = default)
        {
            onInstall();
            return Task.FromResult(new TrtRtxEpInstallResult(
                Succeeded: succeeded,
                FailureDetail: succeeded ? null : "Simulated install failure."));
        }
    }

    private sealed class CountingTensorRtRtxReadinessProbe(Func<TensorRtRtxReadinessReport> factory)
        : ITensorRtRtxReadinessProbe
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<TensorRtRtxReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(factory());
        }
    }
}
