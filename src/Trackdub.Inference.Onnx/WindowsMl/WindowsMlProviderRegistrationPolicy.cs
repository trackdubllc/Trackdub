using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.WindowsMl;

public enum WindowsMlProviderRegistrationRoute
{
    None,
    PackagedDirectMl,
    CatalogExecutionProvider
}

public sealed record WindowsMlProviderRegistrationResult(
    ExecutionProviderKind Provider,
    WindowsMlProviderRegistrationRoute Route,
    WindowsMlBootstrapMode? Mode,
    bool RegistrationSucceeded,
    string Detail);

public sealed class WindowsMlProviderRegistrationPolicy
{
    private readonly Func<CancellationToken, Task<WindowsMlBootstrapResult>> _registerInstalledCertifiedAsync;
    private readonly Func<CancellationToken, Task<WindowsMlBootstrapResult>> _ensureAndRegisterCertifiedAsync;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private WindowsMlBootstrapResult? _registerInstalledCertifiedResult;
    private WindowsMlBootstrapResult? _ensureAndRegisterCertifiedResult;

    // Separate cache for the bulk EnsureAllCertifiedCatalog operation so it does not
    // poison the per-provider session cache used by RegisterForSessionAsync.
    private WindowsMlBootstrapResult? _ensureAllCertifiedCatalogResult;

    public static WindowsMlProviderRegistrationPolicy Shared { get; } = CreateShared();

    public WindowsMlProviderRegistrationPolicy(
        Func<CancellationToken, Task<WindowsMlBootstrapResult>> registerInstalledCertifiedAsync,
        Func<CancellationToken, Task<WindowsMlBootstrapResult>> ensureAndRegisterCertifiedAsync)
    {
        _registerInstalledCertifiedAsync = registerInstalledCertifiedAsync;
        _ensureAndRegisterCertifiedAsync = ensureAndRegisterCertifiedAsync;
    }

    public Task<WindowsMlProviderRegistrationResult> RegisterForReadinessAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken) =>
        RegisterAsync(provider, allowProviderDownloads: false, cacheCompletedResult: false, cancellationToken);

    public Task<WindowsMlProviderRegistrationResult> RegisterForSessionAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken) =>
        RegisterAsync(provider, allowProviderDownloads: true, cacheCompletedResult: true, cancellationToken);

    public Task<WindowsMlProviderRegistrationResult> EnsureAllCertifiedCatalogAsync(
        CancellationToken cancellationToken) =>
        EnsureAllCertifiedCatalogCoreAsync(cancellationToken);

    private static WindowsMlProviderRegistrationPolicy CreateShared()
    {
        var bootstrapper = new WindowsMlExecutionProviderBootstrapper();
        return new WindowsMlProviderRegistrationPolicy(
            bootstrapper.RegisterInstalledCertifiedAsync,
            bootstrapper.EnsureAndRegisterCertifiedAsync);
    }

    private async Task<WindowsMlProviderRegistrationResult> RegisterAsync(
        ExecutionProviderKind provider,
        bool allowProviderDownloads,
        bool cacheCompletedResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (provider is ExecutionProviderKind.TensorRTRtx)
        {
            return new WindowsMlProviderRegistrationResult(
                provider,
                WindowsMlProviderRegistrationRoute.None,
                Mode: null,
                RegistrationSucceeded: false,
                Detail: "TensorRT RTX uses the standalone ORT EP ABI plugin route; Windows ML catalog registration is skipped.");
        }

        WindowsMlProviderRegistrationRequest request = ResolveRequest(provider, allowProviderDownloads);
        if (request.Mode is null)
        {
            return new WindowsMlProviderRegistrationResult(
                provider,
                request.Route,
                Mode: null,
                RegistrationSucceeded: true,
                Detail: "Windows ML bootstrap skipped for CPU-only provider route.");
        }

        WindowsMlBootstrapResult result = await RunOrGetCachedAsync(request.Mode.Value, cacheCompletedResult, cancellationToken)
            .ConfigureAwait(false);
        return FormatResult(provider, request.Route, result, allowProviderDownloads);
    }

    private async Task<WindowsMlBootstrapResult> RunOrGetCachedAsync(
        WindowsMlBootstrapMode mode,
        bool cacheCompletedResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WindowsMlBootstrapResult? cached = ResolveCachedResult(mode);

            if (cacheCompletedResult && cached != null)
            {
                return cached;
            }

            WindowsMlBootstrapResult result = mode switch
            {
                WindowsMlBootstrapMode.RegisterInstalledCertified => await _registerInstalledCertifiedAsync(cancellationToken)
                    .ConfigureAwait(false),
                WindowsMlBootstrapMode.EnsureAndRegisterCertified =>
                    await _ensureAndRegisterCertifiedAsync(cancellationToken).ConfigureAwait(false),
                WindowsMlBootstrapMode.EnsureAllCertifiedCatalog =>
                    throw new ArgumentOutOfRangeException(
                        nameof(mode),
                        mode,
                        "EnsureAllCertifiedCatalog uses EnsureAllCertifiedCatalogAsync, not the per-session cache."),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Windows ML bootstrap mode.")
            };

            if (cacheCompletedResult)
            {
                if (mode is WindowsMlBootstrapMode.RegisterInstalledCertified)
                {
                    _registerInstalledCertifiedResult = result;
                }
                else
                {
                    _ensureAndRegisterCertifiedResult = result;
                }
            }

            return result;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private WindowsMlBootstrapResult? ResolveCachedResult(WindowsMlBootstrapMode mode)
    {
        if (mode is WindowsMlBootstrapMode.RegisterInstalledCertified &&
            _ensureAndRegisterCertifiedResult?.Succeeded is true)
        {
            return _ensureAndRegisterCertifiedResult;
        }

        return mode switch
        {
            WindowsMlBootstrapMode.RegisterInstalledCertified => _registerInstalledCertifiedResult,
            WindowsMlBootstrapMode.EnsureAndRegisterCertified => _ensureAndRegisterCertifiedResult,
            WindowsMlBootstrapMode.EnsureAllCertifiedCatalog =>
                throw new ArgumentOutOfRangeException(
                    nameof(mode),
                    mode,
                    "EnsureAllCertifiedCatalog uses EnsureAllCertifiedCatalogAsync, not the per-session cache."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Windows ML bootstrap mode.")
        };
    }

    private async Task<WindowsMlProviderRegistrationResult> EnsureAllCertifiedCatalogCoreAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ensureAllCertifiedCatalogResult is not null)
                return FormatAllCertifiedCatalogResult(_ensureAllCertifiedCatalogResult);

            if (_ensureAndRegisterCertifiedResult is not null)
            {
                _ensureAllCertifiedCatalogResult = _ensureAndRegisterCertifiedResult;
                return FormatAllCertifiedCatalogResult(_ensureAndRegisterCertifiedResult);
            }

            WindowsMlBootstrapResult result = await _ensureAndRegisterCertifiedAsync(cancellationToken)
                .ConfigureAwait(false);
            _ensureAndRegisterCertifiedResult = result;
            _ensureAllCertifiedCatalogResult = result;
            return FormatAllCertifiedCatalogResult(result);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static WindowsMlProviderRegistrationResult FormatAllCertifiedCatalogResult(
        WindowsMlBootstrapResult result)
    {
        const string routeDetail =
            "Catalog ensure-and-register completed for all certified providers. Individual execution providers may still be not installed, not ready, or unavailable on this hardware; use per-provider status below or run pipeline discovery.";

        string detail = result.Succeeded
            ? $"Windows ML {routeDetail}"
            : string.IsNullOrWhiteSpace(result.FailureReason)
                ? $"Windows ML catalog ensure-and-register did not complete via {WindowsMlBootstrapMode.EnsureAllCertifiedCatalog}. {routeDetail}"
                : $"Windows ML catalog ensure-and-register did not complete via {WindowsMlBootstrapMode.EnsureAllCertifiedCatalog}. {routeDetail} Failure: {result.FailureReason}";

        return new WindowsMlProviderRegistrationResult(
            ExecutionProviderKind.Cpu,
            WindowsMlProviderRegistrationRoute.CatalogExecutionProvider,
            WindowsMlBootstrapMode.EnsureAllCertifiedCatalog,
            result.Succeeded,
            detail);
    }

    private static WindowsMlProviderRegistrationRequest ResolveRequest(
        ExecutionProviderKind provider,
        bool allowProviderDownloads) =>
        provider switch
        {
            ExecutionProviderKind.Cpu => new WindowsMlProviderRegistrationRequest(
                WindowsMlProviderRegistrationRoute.None,
                Mode: null),
            ExecutionProviderKind.DirectMl => new WindowsMlProviderRegistrationRequest(
                WindowsMlProviderRegistrationRoute.PackagedDirectMl,
                WindowsMlBootstrapMode.RegisterInstalledCertified),
            ExecutionProviderKind.Qnn or ExecutionProviderKind.OpenVinoCatalog or ExecutionProviderKind.VitisAi =>
                new WindowsMlProviderRegistrationRequest(
                    WindowsMlProviderRegistrationRoute.CatalogExecutionProvider,
                    // Per-provider registration (Model Manager / catalog services) owns downloads.
                    // Session/readiness must not call bulk EnsureAndRegisterCertifiedAsync.
                    WindowsMlBootstrapMode.RegisterInstalledCertified),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported execution provider kind.")
        };

    private static WindowsMlProviderRegistrationResult FormatResult(
        ExecutionProviderKind provider,
        WindowsMlProviderRegistrationRoute route,
        WindowsMlBootstrapResult result,
        bool allowProviderDownloads)
    {
        string detail = route switch
        {
            WindowsMlProviderRegistrationRoute.PackagedDirectMl => FormatPackagedDirectMlDetail(result),
            WindowsMlProviderRegistrationRoute.CatalogExecutionProvider => FormatCatalogProviderDetail(result, allowProviderDownloads),
            WindowsMlProviderRegistrationRoute.None => "Windows ML bootstrap skipped for CPU-only provider route.",
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unsupported Windows ML provider route.")
        };

        return new WindowsMlProviderRegistrationResult(
            provider,
            route,
            result.Mode,
            result.Succeeded,
            detail);
    }

    private static string FormatPackagedDirectMlDetail(WindowsMlBootstrapResult result)
    {
        const string routeDetail =
            "WinML catalog DirectML route. Session creation selects a GPU device from OrtEnv.GetEpDevices() and appends that device to SessionOptions; readiness still requires a smoke-test session selecting dml and running the graph.";

        if (result.Succeeded)
        {
            return $"Windows ML bootstrap succeeded via {result.Mode} for {routeDetail}";
        }

        return string.IsNullOrWhiteSpace(result.FailureReason)
            ? $"Windows ML bootstrap did not complete via {result.Mode} for {routeDetail}"
            : $"Windows ML bootstrap did not complete via {result.Mode} for {routeDetail} Failure: {result.FailureReason}";
    }

    private static string FormatCatalogProviderDetail(
        WindowsMlBootstrapResult result,
        bool allowProviderDownloads)
    {
        // Use the actual bootstrap mode to label the route — not the request-level allowProviderDownloads
        // flag, which reflects the session policy rather than whether this call downloaded anything.
        string routeDetail = result.Mode is WindowsMlBootstrapMode.EnsureAndRegisterCertified
            ? "catalog execution provider route (download-capable). Readiness still requires the requested provider to be selected and the graph to run."
            : "catalog execution provider route (installed providers only). Readiness still requires the requested provider to be selected and the graph to run.";

        if (result.Succeeded)
        {
            return $"Windows ML bootstrap succeeded via {result.Mode} for {routeDetail}";
        }

        return string.IsNullOrWhiteSpace(result.FailureReason)
            ? $"Windows ML bootstrap did not complete via {result.Mode} for {routeDetail}"
            : $"Windows ML bootstrap did not complete via {result.Mode} for {routeDetail} Failure: {result.FailureReason}";
    }

    private sealed record WindowsMlProviderRegistrationRequest(
        WindowsMlProviderRegistrationRoute Route,
        WindowsMlBootstrapMode? Mode);
}
