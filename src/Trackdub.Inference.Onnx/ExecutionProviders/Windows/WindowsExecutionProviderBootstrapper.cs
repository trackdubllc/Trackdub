using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Dnnl;
using Trackdub.Inference.Onnx.Migraphx;
using Trackdub.Inference.Onnx.NativeCudaTensorRt;
using Trackdub.Inference.Onnx.OpenVino;
using Trackdub.Inference.Onnx.Qnn;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Onnx.VitisAi;
using Trackdub.Inference.Onnx.WindowsMl;
using Microsoft.ML.OnnxRuntime;
using System.Runtime.Versioning;

namespace Trackdub.Inference.Onnx.ExecutionProviders.Windows;

/// <summary>
/// Windows-specific execution provider bootstrapper.
/// Handles DirectML via Windows ML APIs and TensorRT RTX via the standalone ORT EP ABI plugin.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsExecutionProviderBootstrapper : IExecutionProviderBootstrapper
{
    private readonly WindowsMlProviderRegistrationPolicy _registrationPolicy;
    private readonly INativeCudaTensorRtWindowsPolicy _nativeCudaTensorRtWindowsPolicy;
    private readonly ITensorRtRtxProviderBootstrap _tensorRtRtxPluginBootstrap;
    private readonly WindowsMlMigraphxCatalogService _migraphxCatalog = new();
    private readonly WindowsMlOpenVinoCatalogService _openVinoCatalog = new();
    private readonly WindowsMlQnnCatalogService _qnnCatalog = new();
    private readonly WindowsMlVitisAiCatalogService _vitisAiCatalog = new();
    private readonly IDnnlReadinessProbe _dnnlReadinessProbe = new DnnlReadinessProbe();

    public WindowsExecutionProviderBootstrapper()
        : this(
            WindowsMlProviderRegistrationPolicy.Shared,
            NullNativeCudaTensorRtWindowsPolicy.Instance,
            CreateDefaultTrtRtxBootstrap())
    {
    }

    internal WindowsExecutionProviderBootstrapper(WindowsMlProviderRegistrationPolicy registrationPolicy)
        : this(registrationPolicy, NullNativeCudaTensorRtWindowsPolicy.Instance, CreateDefaultTrtRtxBootstrap())
    {
    }

    internal WindowsExecutionProviderBootstrapper(
        WindowsMlProviderRegistrationPolicy registrationPolicy,
        INativeCudaTensorRtWindowsPolicy nativeCudaTensorRtWindowsPolicy,
        ITensorRtRtxProviderBootstrap tensorRtRtxPluginBootstrap)
    {
        _registrationPolicy = registrationPolicy ?? throw new ArgumentNullException(nameof(registrationPolicy));
        _nativeCudaTensorRtWindowsPolicy = nativeCudaTensorRtWindowsPolicy
            ?? throw new ArgumentNullException(nameof(nativeCudaTensorRtWindowsPolicy));
        _tensorRtRtxPluginBootstrap = tensorRtRtxPluginBootstrap
            ?? throw new ArgumentNullException(nameof(tensorRtRtxPluginBootstrap));
    }

    /// <summary>
    /// Builds a real-provider TRT-RTX bootstrap without Infrastructure or Application dependencies.
    /// Resolves the default installed-bundle path; explicit StudioSettings directory is only
    /// available via the DI-wired <see cref="CompositionRoot"/> path.
    /// </summary>
    private static ITensorRtRtxProviderBootstrap CreateDefaultTrtRtxBootstrap()
    {
        string userDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trackdub");
        return TensorRtRtxProviderBootstrapFactory.CreateWithDefaultInstallPath(userDataRoot);
    }

    public async Task<ExecutionProviderBootstrapResult> BootstrapAsync(
        ExecutionProviderKind provider,
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (provider is ExecutionProviderKind.Cuda or ExecutionProviderKind.TensorRt)
        {
            bool allowNative = await _nativeCudaTensorRtWindowsPolicy
                .IsNativeProvidersAllowedOnWindowsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (allowNative)
            {
                return NativeCudaTensorRtWindowsBootstrap.Bootstrap(provider, allowDownloads);
            }
        }

        // Promote cross-platform enum values to their Windows equivalents
        if (provider == ExecutionProviderKind.Cuda)
            provider = ExecutionProviderKind.DirectMl;
        if (provider == ExecutionProviderKind.TensorRt)
            provider = ExecutionProviderKind.TensorRTRtx;
        if (provider == ExecutionProviderKind.CoreMl)
            return new ExecutionProviderBootstrapResult(
                ExecutionProviderKind.CoreMl, ExecutionProviderKind.Cpu,
                Succeeded: false,
                Detail: "CoreML is not available on Windows.",
                FailureReason: "CoreML is macOS-only.");

        if (provider == ExecutionProviderKind.TensorRTRtx)
        {
            return await BootstrapTensorRtRtxPluginAsync(allowDownloads, cancellationToken).ConfigureAwait(false);
        }

        // CPU doesn't need bootstrap
        if (provider == ExecutionProviderKind.Cpu)
        {
            return new ExecutionProviderBootstrapResult(
                provider,
                ExecutionProviderKind.Cpu,
                Succeeded: true,
                Detail: "CPU provider requires no bootstrap.");
        }

        if (provider == ExecutionProviderKind.Dnnl)
        {
            return await ResolveDnnlAsync(provider, cancellationToken).ConfigureAwait(false);
        }

        if (provider == ExecutionProviderKind.Migraphx)
        {
            MigraphxBootstrapResult migraphx = await _migraphxCatalog
                .EnsureRegisteredAsync(allowDownloads, cancellationToken)
                .ConfigureAwait(false);
            return new ExecutionProviderBootstrapResult(
                ExecutionProviderKind.Migraphx,
                ExecutionProviderKind.Migraphx,
                migraphx.Succeeded,
                migraphx.Detail,
                migraphx.Succeeded ? null : migraphx.Detail);
        }

        ExecutionProviderBootstrapResult? winMlCatalogEp = await TryBootstrapWinMlCatalogEpAsync(
                provider,
                allowDownloads,
                cancellationToken)
            .ConfigureAwait(false);
        if (winMlCatalogEp is not null)
        {
            return winMlCatalogEp;
        }

        // For GPU providers, use the registration policy (cached internally)
        var registrationResult = allowDownloads
            ? await _registrationPolicy.RegisterForSessionAsync(provider, cancellationToken).ConfigureAwait(false)
            : await _registrationPolicy.RegisterForReadinessAsync(provider, cancellationToken).ConfigureAwait(false);

        ExecutionProviderKind selectedProvider = await DetermineFallbackProviderAsync(
            registrationResult,
            cancellationToken).ConfigureAwait(false);

        return new ExecutionProviderBootstrapResult(
            RequestedProvider: provider,
            SelectedProvider: selectedProvider,
            Succeeded: registrationResult.RegistrationSucceeded,
            Detail: BuildResultDetail(registrationResult, selectedProvider),
            FailureReason: registrationResult.RegistrationSucceeded ? null : registrationResult.Detail);
    }

    public async Task<ExecutionProviderBootstrapResult> CheckReadinessAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (provider is ExecutionProviderKind.Cuda or ExecutionProviderKind.TensorRt)
        {
            bool allowNative = await _nativeCudaTensorRtWindowsPolicy
                .IsNativeProvidersAllowedOnWindowsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (allowNative)
            {
                return NativeCudaTensorRtWindowsBootstrap.Bootstrap(provider, allowDownloads: false);
            }
        }

        // Promote cross-platform enum values to their Windows equivalents
        if (provider == ExecutionProviderKind.Cuda)
            provider = ExecutionProviderKind.DirectMl;
        if (provider == ExecutionProviderKind.TensorRt)
            provider = ExecutionProviderKind.TensorRTRtx;
        if (provider == ExecutionProviderKind.CoreMl)
            return new ExecutionProviderBootstrapResult(
                ExecutionProviderKind.CoreMl, ExecutionProviderKind.Cpu,
                Succeeded: false,
                Detail: "CoreML is not available on Windows.",
                FailureReason: "CoreML is macOS-only.");

        if (provider == ExecutionProviderKind.OpenVino)
            return new ExecutionProviderBootstrapResult(
                ExecutionProviderKind.OpenVino, ExecutionProviderKind.Cpu,
                Succeeded: false,
                Detail: "OpenVino (standalone) is Linux-only. Use OpenVinoCatalog for the Windows ML catalog EP on Windows.",
                FailureReason: "OpenVino standalone EP is not supported on Windows.");

        if (provider == ExecutionProviderKind.TensorRTRtx)
        {
            return await BootstrapTensorRtRtxPluginAsync(allowProviderDownloads: false, cancellationToken)
                .ConfigureAwait(false);
        }

        if (provider == ExecutionProviderKind.Cpu)
        {
            return new ExecutionProviderBootstrapResult(
                provider,
                ExecutionProviderKind.Cpu,
                Succeeded: true,
                Detail: "CPU provider is always ready.");
        }

        if (provider == ExecutionProviderKind.Dnnl)
        {
            return await ResolveDnnlAsync(provider, cancellationToken).ConfigureAwait(false);
        }

        if (provider == ExecutionProviderKind.Migraphx)
        {
            MigraphxBootstrapResult migraphx = await _migraphxCatalog
                .EnsureRegisteredAsync(allowProviderDownloads: false, cancellationToken)
                .ConfigureAwait(false);
            return new ExecutionProviderBootstrapResult(
                ExecutionProviderKind.Migraphx,
                ExecutionProviderKind.Migraphx,
                migraphx.Succeeded,
                migraphx.Detail,
                migraphx.Succeeded ? null : migraphx.Detail);
        }

        ExecutionProviderBootstrapResult? winMlCatalogReadiness = await TryBootstrapWinMlCatalogEpAsync(
                provider,
                allowProviderDownloads: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (winMlCatalogReadiness is not null)
        {
            return winMlCatalogReadiness;
        }

        var registrationResult = await _registrationPolicy.RegisterForReadinessAsync(provider, cancellationToken)
            .ConfigureAwait(false);

        ExecutionProviderKind selectedProvider = await DetermineFallbackProviderAsync(
            registrationResult,
            cancellationToken).ConfigureAwait(false);

        return new ExecutionProviderBootstrapResult(
            RequestedProvider: provider,
            SelectedProvider: selectedProvider,
            Succeeded: registrationResult.RegistrationSucceeded,
            Detail: BuildResultDetail(registrationResult, selectedProvider),
            FailureReason: registrationResult.RegistrationSucceeded ? null : registrationResult.Detail);
    }

    private async Task<ExecutionProviderBootstrapResult> BootstrapTensorRtRtxPluginAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        // Bootstrap never triggers bundle downloads — that requires explicit user consent via
        // Model Manager Install, CLI `providers trt-rtx install`, or a future setup wizard.
        _ = allowProviderDownloads;
        const bool allowDownloadsDuringBootstrap = false;

        TensorRtRtxBootstrapResult plugin = await _tensorRtRtxPluginBootstrap
            .EnsureRegisteredAsync(allowDownloadsDuringBootstrap, cancellationToken)
            .ConfigureAwait(false);

        if (plugin.Succeeded)
        {
            return new ExecutionProviderBootstrapResult(
                ExecutionProviderKind.TensorRTRtx,
                ExecutionProviderKind.TensorRTRtx,
                Succeeded: true,
                Detail: plugin.Detail,
                FailureReason: null);
        }

        WindowsMlProviderRegistrationResult directMlFallback = await _registrationPolicy
            .RegisterForReadinessAsync(ExecutionProviderKind.DirectMl, cancellationToken)
            .ConfigureAwait(false);
        ExecutionProviderKind selectedProvider = directMlFallback.RegistrationSucceeded
            ? ExecutionProviderKind.DirectMl
            : ExecutionProviderKind.Cpu;
        string detail = selectedProvider is ExecutionProviderKind.DirectMl
            ? $"{plugin.Detail} Verified fallback: DirectMl."
            : $"{plugin.Detail} DirectML fallback was not verified; using CPU.";

        return new ExecutionProviderBootstrapResult(
            ExecutionProviderKind.TensorRTRtx,
            selectedProvider,
            Succeeded: false,
            Detail: detail,
            FailureReason: plugin.Detail);
    }

    private async Task<ExecutionProviderBootstrapResult?> TryBootstrapWinMlCatalogEpAsync(
        ExecutionProviderKind provider,
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        if (provider is not (ExecutionProviderKind.OpenVinoCatalog or ExecutionProviderKind.Qnn or ExecutionProviderKind.VitisAi))
        {
            return null;
        }

        // Bootstrap never triggers downloads — that path requires explicit user consent
        // via ILicenseConsentService (surfaced in Model Manager). Pass false unconditionally
        // so that session-driven bootstrap cannot silently install vendor EPs.
        _ = allowProviderDownloads; // intentionally ignored here
        const bool allowDownloadsDuringBootstrap = false;

        WinMlCatalogBootstrapResult bootstrap = provider switch
        {
            ExecutionProviderKind.OpenVinoCatalog => await _openVinoCatalog
                .EnsureRegisteredAsync(allowDownloadsDuringBootstrap, cancellationToken)
                .ConfigureAwait(false),
            ExecutionProviderKind.Qnn => await _qnnCatalog
                .EnsureRegisteredAsync(allowDownloadsDuringBootstrap, cancellationToken)
                .ConfigureAwait(false),
            ExecutionProviderKind.VitisAi => await _vitisAiCatalog
                .EnsureRegisteredAsync(allowDownloadsDuringBootstrap, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unexpected WinML catalog provider.")
        };

        return new ExecutionProviderBootstrapResult(
            provider,
            provider,
            bootstrap.Succeeded,
            bootstrap.Detail,
            bootstrap.Succeeded ? null : bootstrap.Detail);
    }

    private async Task<ExecutionProviderBootstrapResult> ResolveDnnlAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        DnnlReadinessReport report = await _dnnlReadinessProbe
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        return report.IsReady
            ? new(provider, provider, Succeeded: true, Detail: report.Detail)
            : new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                Detail: $"{report.Detail} Falling back to CPU.",
                FailureReason: report.Detail);
    }

    /// <summary>
    /// When registration fails, pick a fallback only after verifying the candidate EP is actually registrable.
    /// </summary>
    private async Task<ExecutionProviderKind> DetermineFallbackProviderAsync(
        WindowsMlProviderRegistrationResult result,
        CancellationToken cancellationToken)
    {
        if (result.RegistrationSucceeded)
        {
            return result.Provider;
        }

        ExecutionProviderKind candidate = result.Provider switch
        {
            ExecutionProviderKind.TensorRTRtx => ExecutionProviderKind.DirectMl,
            ExecutionProviderKind.Migraphx => ExecutionProviderKind.DirectMl,
            ExecutionProviderKind.CoreMl => ExecutionProviderKind.Cpu,
            ExecutionProviderKind.Cuda => ExecutionProviderKind.DirectMl,
            ExecutionProviderKind.TensorRt => ExecutionProviderKind.TensorRTRtx,
            ExecutionProviderKind.DirectMl => ExecutionProviderKind.Cpu,
            _ => ExecutionProviderKind.Cpu,
        };

        if (candidate is ExecutionProviderKind.DirectMl or ExecutionProviderKind.TensorRTRtx)
        {
            WindowsMlProviderRegistrationResult verified = await _registrationPolicy
                .RegisterForReadinessAsync(candidate, cancellationToken)
                .ConfigureAwait(false);

            if (verified.RegistrationSucceeded)
            {
                return candidate;
            }

            return ExecutionProviderKind.Cpu;
        }

        return candidate;
    }

    private static string BuildResultDetail(
        WindowsMlProviderRegistrationResult result,
        ExecutionProviderKind selectedProvider)
    {
        if (result.RegistrationSucceeded || selectedProvider == result.Provider)
        {
            return result.Detail;
        }

        return $"{result.Detail} Verified fallback: {selectedProvider}.";
    }

    private sealed class NullNativeCudaTensorRtWindowsPolicy : INativeCudaTensorRtWindowsPolicy
    {
        public static NullNativeCudaTensorRtWindowsPolicy Instance { get; } = new();

        public Task<bool> IsNativeProvidersAllowedOnWindowsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
