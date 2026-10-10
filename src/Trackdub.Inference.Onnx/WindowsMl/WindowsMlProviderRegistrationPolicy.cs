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
    // A partial success (some providers failed or are still preparing) is reused only briefly, so
    // sessions do not each rerun a multi-minute catalog bootstrap, yet a provider that finishes
    // preparing is still picked up without an explicit Invalidate().
    private static readonly long PartialSuccessReuseMilliseconds = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private CachedBootstrap? _registerInstalledCertifiedResult;
    private CachedBootstrap? _ensureAndRegisterCertifiedResult;

    // Separate cache for the bulk EnsureAllCertifiedCatalog operation so it does not
    // poison the per-provider session cache used by RegisterForSessionAsync.
    private CachedBootstrap? _ensureAllCertifiedCatalogResult;

    // Bumped by Invalidate(); a bootstrap that started before an invalidation does not cache.
    private int _generation;

    private sealed record CachedBootstrap(WindowsMlBootstrapResult Result, long CachedAtTicks);

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

    /// <summary>
    /// Drops cached bulk results so the next registration reads the catalog again.
    /// Call this after an install or preparation attempt. Failures are not cached;
    /// this still clears a prior success that should not hide a new provider.
    /// Never waits for a bootstrap in progress: that bootstrap's result is simply not cached.
    /// </summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _registerInstalledCertifiedResult, null);
        Volatile.Write(ref _ensureAndRegisterCertifiedResult, null);
        Volatile.Write(ref _ensureAllCertifiedCatalogResult, null);
    }

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
            int generation = Volatile.Read(ref _generation);
            WindowsMlBootstrapResult? cached = ResolveCachedResult(mode);

            if (cacheCompletedResult && cached is not null)
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

            if (cacheCompletedResult && TryCreateCacheEntry(result, generation, out CachedBootstrap? entry))
            {
                if (mode is WindowsMlBootstrapMode.RegisterInstalledCertified)
                {
                    Volatile.Write(ref _registerInstalledCertifiedResult, entry);
                }
                else
                {
                    Volatile.Write(ref _ensureAndRegisterCertifiedResult, entry);
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
        WindowsMlBootstrapResult? ensured = Reusable(Volatile.Read(ref _ensureAndRegisterCertifiedResult));
        if (mode is WindowsMlBootstrapMode.RegisterInstalledCertified && ensured is not null)
        {
            return ensured;
        }

        return mode switch
        {
            WindowsMlBootstrapMode.RegisterInstalledCertified => Reusable(Volatile.Read(ref _registerInstalledCertifiedResult)),
            WindowsMlBootstrapMode.EnsureAndRegisterCertified => ensured,
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
            int generation = Volatile.Read(ref _generation);
            WindowsMlBootstrapResult? cached = Reusable(Volatile.Read(ref _ensureAllCertifiedCatalogResult));
            if (cached is not null)
                return FormatAllCertifiedCatalogResult(cached);

            CachedBootstrap? ensured = Volatile.Read(ref _ensureAndRegisterCertifiedResult);
            if (Reusable(ensured) is { } ensuredResult)
            {
                Volatile.Write(ref _ensureAllCertifiedCatalogResult, ensured);
                return FormatAllCertifiedCatalogResult(ensuredResult);
            }

            WindowsMlBootstrapResult result = await _ensureAndRegisterCertifiedAsync(cancellationToken)
                .ConfigureAwait(false);
            if (TryCreateCacheEntry(result, generation, out CachedBootstrap? entry))
            {
                Volatile.Write(ref _ensureAndRegisterCertifiedResult, entry);
                Volatile.Write(ref _ensureAllCertifiedCatalogResult, entry);
            }

            return FormatAllCertifiedCatalogResult(result);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private bool TryCreateCacheEntry(WindowsMlBootstrapResult result, int generation, out CachedBootstrap? entry)
    {
        entry = result.Succeeded && Volatile.Read(ref _generation) == generation
            ? new CachedBootstrap(result, Environment.TickCount64)
            : null;
        return entry is not null;
    }

    private static WindowsMlBootstrapResult? Reusable(CachedBootstrap? cached)
    {
        if (cached is null || !cached.Result.Succeeded)
        {
            return null;
        }

        bool partial = !string.IsNullOrWhiteSpace(cached.Result.FailureReason);
        return !partial || Environment.TickCount64 - cached.CachedAtTicks < PartialSuccessReuseMilliseconds
            ? cached.Result
            : null;
    }

    private static WindowsMlProviderRegistrationResult FormatAllCertifiedCatalogResult(
        WindowsMlBootstrapResult result)
    {
        const string routeDetail =
            "Catalog ensure-and-register completed for all certified providers. Individual execution providers may still be not installed, not ready, or unavailable on this hardware; use per-provider status below or run pipeline discovery.";

        string detail = result.Succeeded
            ? string.IsNullOrWhiteSpace(result.FailureReason)
                ? $"Windows ML {routeDetail}"
                : $"Windows ML {routeDetail} Some providers are not ready yet (for example, still preparing): {result.FailureReason}"
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
