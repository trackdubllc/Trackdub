using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.Runtime.Planning;

/// <summary>
/// Thread-safe memoizing base for execution-provider readiness probes. The first
/// <c>ProbeAsync</c> for a given <c>allowProviderDownloads</c> value is executed once and its
/// result is reused by every subsequent caller, so a single <c>providers list</c> invocation no
/// longer runs the same native/ORT readiness check twice (once via
/// <see cref="OnnxExecutionProviderDiscovery"/> and again via the *RuntimeReadinessService
/// wrappers, which share the same probe singleton).
/// </summary>
/// <remarks>
/// The cache keys on <c>allowProviderDownloads</c> so an <c>allowProviderDownloads:true</c> install
/// path is never served a value cached from an <c>allowProviderDownloads:false</c> probe. Concurrent
/// callers (BuildRemediationsAsync fans out via Task.WhenAll) share one in-flight probe rather than
/// racing. The cached value is always the actual first probe result: readiness is never fabricated.
/// A faulted or cancelled probe is not cached permanently, so a later call re-probes.
/// </remarks>
public abstract class CachingReadinessProbe<TReport>
{
    private readonly Func<bool, CancellationToken, Task<TReport>> _probe;
    private readonly object _gate = new();
    private Task<TReport>? _cachedForDownloadsDisabled;
    private Task<TReport>? _cachedForDownloadsEnabled;

    protected CachingReadinessProbe(Func<bool, CancellationToken, Task<TReport>> probe) =>
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    protected Task<TReport> ProbeCachedAsync(bool allowProviderDownloads, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Task<TReport>? existing = allowProviderDownloads
                ? _cachedForDownloadsEnabled
                : _cachedForDownloadsDisabled;

            // Only reuse a still-pending or successfully completed probe. A faulted or cancelled
            // task is discarded so the next caller re-runs the actual probe.
            if (existing is not null &&
                existing.Status is not TaskStatus.Faulted and not TaskStatus.Canceled)
            {
                return existing;
            }

            Task<TReport> started = _probe(allowProviderDownloads, cancellationToken);

            if (allowProviderDownloads)
            {
                _cachedForDownloadsEnabled = started;
            }
            else
            {
                _cachedForDownloadsDisabled = started;
            }

            return started;
        }
    }

    /// <summary>
    /// Invalidates all cached probe results, forcing the next <c>ProbeAsync</c> call to re-run
    /// the underlying probe. Call this after state-changing operations (installs, registrations,
    /// downloads) to ensure subsequent probes reflect the new system state rather than returning
    /// stale cached reports.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cachedForDownloadsDisabled = null;
            _cachedForDownloadsEnabled = null;
        }
    }
}

/// <summary>Caching decorator for <see cref="ITensorRtRtxReadinessProbe"/>.</summary>
public sealed class CachingTensorRtRtxReadinessProbe(ITensorRtRtxReadinessProbe inner)
    : CachingReadinessProbe<TensorRtRtxReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        ITensorRtRtxReadinessProbe
{
    public Task<TensorRtRtxReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}

/// <summary>Caching decorator for <see cref="IMigraphxReadinessProbe"/>.</summary>
public sealed class CachingMigraphxReadinessProbe(IMigraphxReadinessProbe inner)
    : CachingReadinessProbe<MigraphxReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IMigraphxReadinessProbe
{
    public Task<MigraphxReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}

/// <summary>Caching decorator for <see cref="IDnnlReadinessProbe"/>.</summary>
public sealed class CachingDnnlReadinessProbe(IDnnlReadinessProbe inner)
    : CachingReadinessProbe<DnnlReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IDnnlReadinessProbe
{
    public Task<DnnlReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}

/// <summary>Caching decorator for <see cref="IOpenVinoCatalogReadinessProbe"/>.</summary>
public sealed class CachingOpenVinoCatalogReadinessProbe(IOpenVinoCatalogReadinessProbe inner)
    : CachingReadinessProbe<WinMlCatalogReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IOpenVinoCatalogReadinessProbe
{
    public Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}

/// <summary>Caching decorator for <see cref="IQnnCatalogReadinessProbe"/>.</summary>
public sealed class CachingQnnCatalogReadinessProbe(IQnnCatalogReadinessProbe inner)
    : CachingReadinessProbe<WinMlCatalogReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IQnnCatalogReadinessProbe
{
    public Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}

/// <summary>Caching decorator for <see cref="IVitisAiCatalogReadinessProbe"/>.</summary>
public sealed class CachingVitisAiCatalogReadinessProbe(IVitisAiCatalogReadinessProbe inner)
    : CachingReadinessProbe<WinMlCatalogReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IVitisAiCatalogReadinessProbe
{
    public Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}
