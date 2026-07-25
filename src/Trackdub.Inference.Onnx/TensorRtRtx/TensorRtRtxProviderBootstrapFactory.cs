using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>
/// Builds <see cref="ITensorRtRtxProviderBootstrap"/> instances with the same wiring shape as the app composition root.
/// </summary>
public static class TensorRtRtxProviderBootstrapFactory
{
    public static ITensorRtRtxProviderBootstrap Create(
        Func<CancellationToken, ValueTask<string?>> explicitPluginDirectoryProvider,
        Func<CancellationToken, ValueTask<string?>> defaultInstallDirectoryProvider,
        Func<bool, CancellationToken, ValueTask<TensorRtRtxBundleEnsureResult>>? bundleEnsureAsync = null)
    {
        ArgumentNullException.ThrowIfNull(explicitPluginDirectoryProvider);
        ArgumentNullException.ThrowIfNull(defaultInstallDirectoryProvider);

        return new TensorRtRtxPluginService(
            explicitPluginDirectoryProvider,
            defaultInstallDirectoryProvider,
            bundleEnsureAsync);
    }

    /// <summary>
    /// Creates a bootstrap instance that resolves the plugin from the default installed-bundle
    /// path only. The explicit <c>StudioSettings.TensorRtRtxPluginDirectory</c> setting is not
    /// available in this layer (no Infrastructure dependency); use the full
    /// <see cref="Create"/> overload when a settings service is available (e.g. CompositionRoot).
    /// Bundle downloads are never triggered; pass <c>userDataRoot</c> computed from
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> + "Trackdub".
    /// </summary>
    public static ITensorRtRtxProviderBootstrap CreateWithDefaultInstallPath(string userDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);

        return Create(
            static _ => ValueTask.FromResult<string?>(null),
            cancellationToken =>
            {
                _ = cancellationToken;
                string? rid = OperatingSystem.IsWindows() ? "win-x64"
                    : OperatingSystem.IsLinux() ? "linux-x64"
                    : null;
                if (rid is null)
                {
                    return ValueTask.FromResult<string?>(null);
                }

                string installDirectory = TensorRtRtxProviderConstants.GetDefaultInstallDirectory(
                    userDataRoot, rid);
                return ValueTask.FromResult<string?>(installDirectory);
            },
            bundleEnsureAsync: null);
    }
}
