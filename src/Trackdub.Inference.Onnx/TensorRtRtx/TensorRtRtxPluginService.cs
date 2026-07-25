using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

public sealed record TensorRtRtxBundleEnsureResult(
    bool Succeeded,
    string? InstallDirectory,
    string? Detail);

internal sealed class TensorRtRtxPluginService : ITensorRtRtxProviderBootstrap
{
    // Must equal the canonical EP name: ORT reports OrtEpDevice.EpName as the registration name passed to
    // RegisterExecutionProviderLibrary (per ORT plugin-EP contract). Registering under a custom handle made
    // the device invisible to IsPluginProviderListed / IsTensorRtRtxDeviceCandidate, which match this name.
    private const string RegistrationName = TensorRtRtxProviderConstants.PluginOrtExecutionProviderName;
    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);
    private static string? registeredProviderLibraryPath;

    private readonly Func<CancellationToken, ValueTask<string?>> explicitPluginDirectoryProvider;
    private readonly Func<CancellationToken, ValueTask<string?>> defaultInstallDirectoryProvider;
    private readonly Func<bool, CancellationToken, ValueTask<TensorRtRtxBundleEnsureResult>>? bundleEnsureAsync;

    public static TensorRtRtxPluginService Shared { get; } = new();

    public TensorRtRtxPluginService()
        : this(
            static _ => ValueTask.FromResult<string?>(null),
            static _ => ValueTask.FromResult<string?>(null),
            bundleEnsureAsync: null)
    {
    }

    public TensorRtRtxPluginService(
        Func<CancellationToken, ValueTask<string?>> explicitPluginDirectoryProvider,
        Func<CancellationToken, ValueTask<string?>> defaultInstallDirectoryProvider,
        Func<bool, CancellationToken, ValueTask<TensorRtRtxBundleEnsureResult>>? bundleEnsureAsync = null)
    {
        this.explicitPluginDirectoryProvider = explicitPluginDirectoryProvider
            ?? throw new ArgumentNullException(nameof(explicitPluginDirectoryProvider));
        this.defaultInstallDirectoryProvider = defaultInstallDirectoryProvider
            ?? throw new ArgumentNullException(nameof(defaultInstallDirectoryProvider));
        this.bundleEnsureAsync = bundleEnsureAsync;
    }

    public async Task<TensorRtRtxBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (bool eligible, TensorRtRtxReadinessBlocker hardwareBlocker, string hardwareDetail) = EvaluateHardwareEligibility();
        if (!eligible)
        {
            return new TensorRtRtxBootstrapResult(
                false,
                TensorRtRtxProviderIds.PluginEpAbi,
                hardwareBlocker,
                hardwareDetail);
        }

        // If another instance (e.g. the DI-wired bootstrap) has already registered the plugin and
        // a matching GPU OrtEpDevice is visible, this instance can report success without needing to
        // resolve a plugin directory itself. This keeps mixed Shared/DI instances consistent.
        if (registeredProviderLibraryPath is not null && TensorRtRtxOrtProbe.IsPluginProviderListed())
        {
            return new TensorRtRtxBootstrapResult(
                true,
                TensorRtRtxProviderIds.PluginEpAbi,
                null,
                $"TensorRT RTX EP ABI plugin already registered from '{registeredProviderLibraryPath}'.");
        }

        string? explicitPluginDirectory = await explicitPluginDirectoryProvider(cancellationToken)
            .ConfigureAwait(false);
        string? defaultInstallDirectory = await defaultInstallDirectoryProvider(cancellationToken)
            .ConfigureAwait(false);
        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory,
            defaultInstallDirectory);

        if (!resolution.Succeeded &&
            allowProviderDownloads &&
            bundleEnsureAsync is not null &&
            resolution.Blocker is TensorRtRtxReadinessBlocker.EpNotPresent or TensorRtRtxReadinessBlocker.EpNotReady)
        {
            TensorRtRtxBundleEnsureResult ensureResult = await bundleEnsureAsync(true, cancellationToken)
                .ConfigureAwait(false);
            if (!ensureResult.Succeeded)
            {
                return new TensorRtRtxBootstrapResult(
                    false,
                    TensorRtRtxProviderIds.PluginEpAbi,
                    TensorRtRtxReadinessBlocker.EpDownloadFailed,
                    string.IsNullOrWhiteSpace(ensureResult.Detail)
                        ? "TensorRT RTX EP bundle download did not complete."
                        : ensureResult.Detail);
            }

            explicitPluginDirectory = await explicitPluginDirectoryProvider(cancellationToken).ConfigureAwait(false);
            defaultInstallDirectory = await defaultInstallDirectoryProvider(cancellationToken).ConfigureAwait(false);
            resolution = TensorRtRtxPluginLocator.Resolve(explicitPluginDirectory, defaultInstallDirectory);
        }

        if (!resolution.Succeeded || string.IsNullOrWhiteSpace(resolution.ProviderLibraryPath))
        {
            return new TensorRtRtxBootstrapResult(
                false,
                TensorRtRtxProviderIds.PluginEpAbi,
                resolution.Blocker,
                resolution.Detail);
        }

        try
        {
            await RegistrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.Equals(registeredProviderLibraryPath, resolution.ProviderLibraryPath, StringComparison.OrdinalIgnoreCase))
                {
                    OrtEnv.Instance().RegisterExecutionProviderLibrary(
                        RegistrationName,
                        resolution.ProviderLibraryPath);
                    registeredProviderLibraryPath = resolution.ProviderLibraryPath;
                }
            }
            finally
            {
                RegistrationGate.Release();
            }

            bool providerListed = TensorRtRtxOrtProbe.IsPluginProviderListed();
            if (!providerListed)
            {
                return new TensorRtRtxBootstrapResult(
                    false,
                    TensorRtRtxProviderIds.PluginEpAbi,
                    TensorRtRtxReadinessBlocker.OrtProviderUnavailable,
                    $"{TensorRtRtxProviderConstants.PluginOrtExecutionProviderName} plugin library registered, but no matching GPU OrtEpDevice is visible.");
            }

            return new TensorRtRtxBootstrapResult(
                true,
                TensorRtRtxProviderIds.PluginEpAbi,
                null,
                $"TensorRT RTX EP ABI plugin registered from '{resolution.ProviderLibraryPath}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return new TensorRtRtxBootstrapResult(
                false,
                TensorRtRtxProviderIds.PluginEpAbi,
                TensorRtRtxReadinessBlocker.EpRegisterFailed,
                $"TensorRT RTX EP ABI plugin registration failed: {ex.Message}");
        }
    }

    private static (bool Eligible, TensorRtRtxReadinessBlocker Blocker, string Detail) EvaluateHardwareEligibility()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return WindowsNvidiaHardwareGate.Evaluate();
        }
#endif

#if LINUX
        if (OperatingSystem.IsLinux())
        {
            return LinuxNvidiaHardwareGate.Evaluate();
        }
#endif

        return (false, TensorRtRtxReadinessBlocker.PlatformUnsupported,
            "TensorRT RTX EP ABI plugin registration is supported on Windows and Linux only.");
    }
}
