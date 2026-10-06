using Trackdub.Application.Dubbing;
using Trackdub.Contracts;
using Trackdub.Licensing;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Composition.Headless;

/// <summary>
/// Thread-safe factory that creates per-project <see cref="IDubbingSession"/> instances
/// from a root <see cref="ServiceProvider"/> built by headless composition.
/// </summary>
public sealed class HeadlessDubbingSessionFactory : IDubbingSessionFactory
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _licenseInitGate = new();
    private volatile bool _disposed;
    private volatile bool _licenseInitialized;

    public HeadlessDubbingSessionFactory(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        // Centralize eager activation here so every headless host path—including advanced
        // callers that build a provider from AddHeadlessTrackdub directly—applies custom
        // storage environment values before any session or static consumer can run. The
        // resolved singleton remains owned and disposed by this provider.
        serviceProvider.GetService<HeadlessStorageEnvironmentScope>();

        _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <inheritdoc />
    public IDubbingSession CreateSession(string projectRootPath, StudioSettings? settings = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

        EnsureLicenseInitialized();

        IServiceScope scope = _scopeFactory.CreateScope();
        try
        {
            return new HeadlessDubbingSession(scope, projectRootPath, settings);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _serviceProvider.Dispose();
    }

    private void EnsureLicenseInitialized()
    {
        if (_licenseInitialized)
            return;

        lock (_licenseInitGate)
        {
            if (_licenseInitialized)
                return;

            ILicenseInitializer? initializer = _serviceProvider.GetService<ILicenseInitializer>();
            if (initializer is null)
            {
                _licenseInitialized = true;
                return;
            }

            initializer.InitializeAsync().GetAwaiter().GetResult();
            _licenseInitialized = true;
        }
    }
}
