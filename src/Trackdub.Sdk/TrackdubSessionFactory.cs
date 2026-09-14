using Trackdub.Application.Dubbing;
using Trackdub.Composition.Headless;
using Trackdub.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Sdk;

/// <summary>
/// Thread-safe factory that creates per-project <see cref="TrackdubSession"/> instances.
/// Wraps the root <see cref="IServiceProvider"/> built by <see cref="TrackdubBuilder"/>.
/// </summary>
public sealed class TrackdubSessionFactory : IDubbingSessionFactory, IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly HeadlessDubbingSessionFactory _inner;
    private volatile bool _disposed;

    internal TrackdubSessionFactory(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _inner = new HeadlessDubbingSessionFactory(serviceProvider);
    }

    /// <summary>
    /// Creates a new session scoped to the specified project root path using default session options.
    /// </summary>
    public TrackdubSession CreateSession(string projectRootPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new TrackdubSession(_inner.CreateSession(projectRootPath));
    }

    /// <summary>
    /// Creates a new session scoped to the specified project root path with additional session options.
    /// </summary>
    public TrackdubSession CreateSession(string projectRootPath, SdkSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new TrackdubSession(_inner.CreateSession(projectRootPath, TrackdubSession.ToStudioSettings(options)));
    }

    /// <inheritdoc />
    IDubbingSession IDubbingSessionFactory.CreateSession(string projectRootPath, StudioSettings? settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inner.CreateSession(projectRootPath, settings);
    }

    internal T GetRequiredService<T>() where T : notnull
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _serviceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Writes NVIDIA TensorRT RTX license acceptance to the disk settings file used by
    /// headless hosts. The in-memory overlay does not persist across processes.
    /// </summary>
    public Task PersistNvidiaTensorRtRtxLicenseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IAppStoragePaths storagePaths = _serviceProvider.GetRequiredService<IAppStoragePaths>();
        return HeadlessPersistedEpLicenses.PersistNvidiaTensorRtRtxAcceptanceAsync(
            storagePaths,
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _inner.Dispose();
    }
}
