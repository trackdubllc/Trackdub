using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.ExecutionProviders;
using Trackdub.Inference.Onnx.ExecutionProviders.Windows;
using Trackdub.Inference.Onnx.WindowsMl;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Tests;

public sealed class WindowsMlProviderRegistrationPolicyTests
{
    [Fact]
    public async Task BootstrapAsync_TensorRtRtxUsesPluginBootstrapWithoutWindowsMlCatalogRegistration()
    {
        var pluginCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
                throw new InvalidOperationException("Windows ML RegisterInstalledCertified must not run for TRT RTX."),
            ensureAndRegisterCertifiedAsync: _ =>
                throw new InvalidOperationException("Windows ML EnsureAndRegisterCertified must not run for TRT RTX."));
        var bootstrapper = new WindowsExecutionProviderBootstrapper(
            policy,
            new StubNativeCudaTensorRtWindowsPolicy(allowed: false),
            new StubTensorRtRtxProviderBootstrap(async (_, cancellationToken) =>
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                pluginCalls++;
                return new TensorRtRtxBootstrapResult(
                    Succeeded: true,
                    ProviderId: TensorRtRtxProviderIds.PluginEpAbi,
                    Blocker: null,
                    Detail: "plugin registered");
            }));

        ExecutionProviderBootstrapResult result = await bootstrapper
            .BootstrapAsync(ExecutionProviderKind.TensorRTRtx, allowDownloads: true, CancellationToken.None);

        Assert.Equal(1, pluginCalls);
        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.TensorRTRtx, result.SelectedProvider);
        Assert.Contains("plugin registered", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapAsync_TensorRtRtxIgnoresAllowDownloadsDuringBootstrap()
    {
        bool? capturedAllowDownloads = null;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
                Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null)),
            ensureAndRegisterCertifiedAsync: _ =>
                Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null)));
        var bootstrapper = new WindowsExecutionProviderBootstrapper(
            policy,
            new StubNativeCudaTensorRtWindowsPolicy(allowed: false),
            new StubTensorRtRtxProviderBootstrap((allowProviderDownloads, cancellationToken) =>
            {
                capturedAllowDownloads = allowProviderDownloads;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new TensorRtRtxBootstrapResult(
                    Succeeded: false,
                    ProviderId: TensorRtRtxProviderIds.PluginEpAbi,
                    Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
                    Detail: "plugin missing"));
            }));

        await bootstrapper.BootstrapAsync(ExecutionProviderKind.TensorRTRtx, allowDownloads: true, CancellationToken.None);

        Assert.False(capturedAllowDownloads);
    }

    [Fact]
    public async Task BootstrapAsync_TensorRtRtxFallsBackToDirectMlWhenBundleNotInstalled()
    {
        bool? capturedAllowDownloads = null;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
                Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null)),
            ensureAndRegisterCertifiedAsync: _ =>
                Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null)));
        var bootstrapper = new WindowsExecutionProviderBootstrapper(
            policy,
            new StubNativeCudaTensorRtWindowsPolicy(allowed: false),
            new StubTensorRtRtxProviderBootstrap((allowProviderDownloads, cancellationToken) =>
            {
                capturedAllowDownloads = allowProviderDownloads;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new TensorRtRtxBootstrapResult(
                    Succeeded: false,
                    ProviderId: TensorRtRtxProviderIds.PluginEpAbi,
                    Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
                    Detail: "TensorRT RTX EP ABI plugin bundle is not installed."));
            }));

        ExecutionProviderBootstrapResult result = await bootstrapper
            .BootstrapAsync(ExecutionProviderKind.TensorRTRtx, allowDownloads: true, CancellationToken.None);

        Assert.False(capturedAllowDownloads);
        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.DirectMl, result.SelectedProvider);
        Assert.Contains("Verified fallback: DirectMl", result.Detail, StringComparison.Ordinal);
        Assert.Contains("bundle is not installed", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterForSessionAsync_DirectMlRegistersInstalledCertifiedOnceAndReportsPackagedRoute()
    {
        var registerCalls = 0;
        var ensureCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null));
            },
            ensureAndRegisterCertifiedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null));
            });

        WindowsMlProviderRegistrationResult first = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.DirectMl, CancellationToken.None);
        WindowsMlProviderRegistrationResult second = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.DirectMl, CancellationToken.None);

        Assert.Equal(1, registerCalls);
        Assert.Equal(0, ensureCalls);
        Assert.True(first.RegistrationSucceeded);
        Assert.True(second.RegistrationSucceeded);
        Assert.Equal(WindowsMlProviderRegistrationRoute.PackagedDirectMl, first.Route);
        Assert.Equal(WindowsMlBootstrapMode.RegisterInstalledCertified, first.Mode);
        Assert.Contains("WinML catalog DirectML route", first.Detail, StringComparison.Ordinal);
        Assert.Contains("RegisterInstalledCertified", first.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterForSessionAsync_DirectMlCachesFailureToAvoidPerSessionBootstrap()
    {
        var registerCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: false,
                    FailureReason: "catalog unavailable"));
            },
            ensureAndRegisterCertifiedAsync: _ =>
            {
                throw new InvalidOperationException("Ensure should not run for DirectML.");
            });

        WindowsMlProviderRegistrationResult first = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.DirectMl, CancellationToken.None);
        WindowsMlProviderRegistrationResult second = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.DirectMl, CancellationToken.None);

        Assert.Equal(1, registerCalls);
        Assert.False(first.RegistrationSucceeded);
        Assert.False(second.RegistrationSucceeded);
        Assert.Contains("catalog unavailable", first.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterForReadinessAsync_DirectMlRunsOncePerReadinessCheck()
    {
        var registerCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null));
            },
            ensureAndRegisterCertifiedAsync: _ =>
            {
                throw new InvalidOperationException("Ensure should not run for DirectML.");
            });

        WindowsMlProviderRegistrationResult first = await policy
            .RegisterForReadinessAsync(ExecutionProviderKind.DirectMl, CancellationToken.None);
        WindowsMlProviderRegistrationResult second = await policy
            .RegisterForReadinessAsync(ExecutionProviderKind.DirectMl, CancellationToken.None);

        Assert.Equal(2, registerCalls);
        Assert.True(first.RegistrationSucceeded);
        Assert.True(second.RegistrationSucceeded);
    }

    [Fact]
    public async Task RegisterForReadinessAsync_TensorRtRtxSkipsWindowsMlCatalog()
    {
        var registerCalls = 0;
        var ensureCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null));
            },
            ensureAndRegisterCertifiedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null));
            });

        WindowsMlProviderRegistrationResult result = await policy
            .RegisterForReadinessAsync(ExecutionProviderKind.TensorRTRtx, CancellationToken.None);

        Assert.Equal(0, registerCalls);
        Assert.Equal(0, ensureCalls);
        Assert.False(result.RegistrationSucceeded);
        Assert.Equal(WindowsMlProviderRegistrationRoute.None, result.Route);
        Assert.Null(result.Mode);
        Assert.Contains("EP ABI plugin", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterForSessionAsync_TensorRtRtxDoesNotEnsureWindowsMlCatalog()
    {
        var registerCalls = 0;
        var ensureCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null));
            },
            ensureAndRegisterCertifiedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null));
            });

        WindowsMlProviderRegistrationResult result = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.TensorRTRtx, CancellationToken.None);

        Assert.Equal(0, registerCalls);
        Assert.Equal(0, ensureCalls);
        Assert.False(result.RegistrationSucceeded);
        Assert.Equal(WindowsMlProviderRegistrationRoute.None, result.Route);
        Assert.Null(result.Mode);
        Assert.Contains("Windows ML catalog registration is skipped", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterForSessionAsync_DirectMlReusesEarlierDownloadCapableEnsure()
    {
        var registerCalls = 0;
        var ensureCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null));
            },
            ensureAndRegisterCertifiedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null));
            });

        WindowsMlProviderRegistrationResult skipped = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.TensorRTRtx, CancellationToken.None);
        WindowsMlProviderRegistrationResult result = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.DirectMl, CancellationToken.None);

        Assert.False(skipped.RegistrationSucceeded);
        Assert.Equal(1, registerCalls);
        Assert.Equal(0, ensureCalls);
        Assert.True(result.RegistrationSucceeded);
        Assert.Equal(WindowsMlProviderRegistrationRoute.PackagedDirectMl, result.Route);
        Assert.Equal(WindowsMlBootstrapMode.RegisterInstalledCertified, result.Mode);
    }

    [Fact]
    public async Task RegisterForReadinessAsync_CpuSkipsWindowsMlCatalog()
    {
        var registerCalls = 0;
        var ensureCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null));
            },
            ensureAndRegisterCertifiedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null));
            });

        WindowsMlProviderRegistrationResult result = await policy
            .RegisterForReadinessAsync(ExecutionProviderKind.Cpu, CancellationToken.None);

        Assert.Equal(0, registerCalls);
        Assert.Equal(0, ensureCalls);
        Assert.True(result.RegistrationSucceeded);
        Assert.Equal(WindowsMlProviderRegistrationRoute.None, result.Route);
        Assert.Null(result.Mode);
        Assert.Contains("CPU-only", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterForSessionAsync_HonorsPreCanceledTokenBeforeRegistration()
    {
        var registerCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null));
            },
            ensureAndRegisterCertifiedAsync: _ =>
            {
                throw new InvalidOperationException("Ensure should not run for DirectML.");
            });
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.RegisterForSessionAsync(ExecutionProviderKind.DirectMl, cancellationSource.Token));

        Assert.Equal(0, registerCalls);
    }
    [Fact]
    public async Task RegisterForSessionAsync_Qnn_UsesRegisterInstalledCertifiedWithoutBulkEnsure()
    {
        var registerCalls = 0;
        var ensureCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null));
            },
            ensureAndRegisterCertifiedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null));
            });

        WindowsMlProviderRegistrationResult result = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.Qnn, CancellationToken.None);

        Assert.Equal(1, registerCalls);
        Assert.Equal(0, ensureCalls);
        Assert.True(result.RegistrationSucceeded);
        Assert.Equal(WindowsMlBootstrapMode.RegisterInstalledCertified, result.Mode);
        Assert.Contains("RegisterInstalledCertified", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterForReadinessAsync_Qnn_UsesRegisterInstalledCertifiedWithoutEnsure()
    {
        var registerCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
            {
                registerCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.RegisterInstalledCertified,
                    Succeeded: true,
                    FailureReason: null));
            },
            ensureAndRegisterCertifiedAsync: _ =>
                throw new InvalidOperationException("Ensure should not run for QNN readiness."));

        WindowsMlProviderRegistrationResult result = await policy
            .RegisterForReadinessAsync(ExecutionProviderKind.Qnn, CancellationToken.None);

        Assert.Equal(1, registerCalls);
        Assert.True(result.RegistrationSucceeded);
        Assert.Equal(WindowsMlProviderRegistrationRoute.CatalogExecutionProvider, result.Route);
    }

    [Fact]
    public async Task EnsureAllCertifiedCatalogAsync_InvokesEnsureDelegateOnceAndCachesSecondCall()
    {
        var ensureCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
                throw new InvalidOperationException("Register should not run for bulk catalog ensure."),
            ensureAndRegisterCertifiedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null));
            });

        WindowsMlProviderRegistrationResult first = await policy
            .EnsureAllCertifiedCatalogAsync(CancellationToken.None);
        WindowsMlProviderRegistrationResult second = await policy
            .EnsureAllCertifiedCatalogAsync(CancellationToken.None);

        Assert.Equal(1, ensureCalls);
        Assert.True(first.RegistrationSucceeded);
        Assert.True(second.RegistrationSucceeded);
        Assert.Equal(WindowsMlBootstrapMode.EnsureAllCertifiedCatalog, first.Mode);
        Assert.Equal(WindowsMlBootstrapMode.EnsureAllCertifiedCatalog, second.Mode);
        Assert.Equal(WindowsMlProviderRegistrationRoute.CatalogExecutionProvider, first.Route);
        Assert.Contains("ensure-and-register completed", first.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Individual execution providers may still be", first.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureAllCertifiedCatalogAsync_DoesNotReuseTensorRtRtxPluginSkip()
    {
        var ensureCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
                throw new InvalidOperationException("Register should not run."),
            ensureAndRegisterCertifiedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null));
            });

        WindowsMlProviderRegistrationResult skipped = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.TensorRTRtx, CancellationToken.None);
        WindowsMlProviderRegistrationResult bulk = await policy
            .EnsureAllCertifiedCatalogAsync(CancellationToken.None);

        Assert.False(skipped.RegistrationSucceeded);
        Assert.Equal(1, ensureCalls);
        Assert.True(bulk.RegistrationSucceeded);
        Assert.Equal(WindowsMlBootstrapMode.EnsureAllCertifiedCatalog, bulk.Mode);
    }

    [Fact]
    public async Task EnsureAllCertifiedCatalogAsync_CachesFailureToAvoidRepeatedCatalogCalls()
    {
        var ensureCalls = 0;
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
                throw new InvalidOperationException("Register should not run."),
            ensureAndRegisterCertifiedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: false,
                    FailureReason: "catalog timed out"));
            });

        WindowsMlProviderRegistrationResult first = await policy
            .EnsureAllCertifiedCatalogAsync(CancellationToken.None);
        WindowsMlProviderRegistrationResult second = await policy
            .EnsureAllCertifiedCatalogAsync(CancellationToken.None);

        Assert.Equal(1, ensureCalls);
        Assert.False(first.RegistrationSucceeded);
        Assert.False(second.RegistrationSucceeded);
        Assert.Equal(WindowsMlBootstrapMode.EnsureAllCertifiedCatalog, first.Mode);
        Assert.Contains("catalog timed out", first.Detail, StringComparison.Ordinal);
        Assert.Contains("catalog timed out", second.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureAllCertifiedCatalogAsync_ReportsDistinctModeFromTensorRtRtxPluginSkip()
    {
        var policy = new WindowsMlProviderRegistrationPolicy(
            registerInstalledCertifiedAsync: _ =>
                throw new InvalidOperationException("Register should not run."),
            ensureAndRegisterCertifiedAsync: _ =>
                Task.FromResult(new WindowsMlBootstrapResult(
                    WindowsMlBootstrapMode.EnsureAndRegisterCertified,
                    Succeeded: true,
                    FailureReason: null)));

        WindowsMlProviderRegistrationResult session = await policy
            .RegisterForSessionAsync(ExecutionProviderKind.TensorRTRtx, CancellationToken.None);
        WindowsMlProviderRegistrationResult bulk = await policy
            .EnsureAllCertifiedCatalogAsync(CancellationToken.None);

        Assert.False(session.RegistrationSucceeded);
        Assert.Null(session.Mode);
        Assert.Equal(WindowsMlBootstrapMode.EnsureAllCertifiedCatalog, bulk.Mode);
    }

    private sealed class StubNativeCudaTensorRtWindowsPolicy(bool allowed) : INativeCudaTensorRtWindowsPolicy
    {
        public Task<bool> IsNativeProvidersAllowedOnWindowsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed);
    }

    private sealed class StubTensorRtRtxProviderBootstrap(
        Func<bool, CancellationToken, Task<TensorRtRtxBootstrapResult>> ensureRegisteredAsync)
        : ITensorRtRtxProviderBootstrap
    {
        public Task<TensorRtRtxBootstrapResult> EnsureRegisteredAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default) =>
            ensureRegisteredAsync(allowProviderDownloads, cancellationToken);
    }
}
