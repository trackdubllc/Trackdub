using System.Reflection;
using System.Runtime.CompilerServices;
using Trackdub.Application.Transcripts;
using Trackdub.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Tests verifying disposal completeness: after session.Dispose(), all scoped services
/// (DB connections, temp files) are released.
///
/// **Validates: Requirements 14.4**
/// </summary>
public sealed class TrackdubSessionDisposalTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    /// <summary>
    /// Tracks whether Dispose() was called on a scoped service.
    /// </summary>
    private sealed class DisposableTracker : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    /// <summary>
    /// A second IDisposable tracker to verify multiple services are all disposed.
    /// </summary>
    private sealed class SecondDisposableTracker : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    /// <summary>
    /// Tracks whether DisposeAsync() was called on a scoped service.
    /// </summary>
    private sealed class AsyncDisposableTracker : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// After session.Dispose(), all scoped IDisposable services are released.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesScopedDisposableServices()
    {
        // Arrange: register DisposableTracker as a scoped service so the DI container owns it.
        var services = new ServiceCollection();
        services.AddScoped<TranscriptWorkspaceContext>();
        services.AddScoped(_ => CreateSafeUninitializedWorkspace());
        services.AddScoped<DisposableTracker>();

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var factory = new TrackdubSessionFactory(serviceProvider);
        string tempDir = CreateTempDirectory();

        var session = factory.CreateSession(tempDir);

        // Resolve the tracker from the session's scope to get the instance the container will dispose.
        DisposableTracker tracker = GetScopedService<DisposableTracker>(session);
        Assert.False(tracker.IsDisposed, "Tracker should not be disposed before session disposal");

        // Act
        session.Dispose();

        // Assert
        Assert.True(tracker.IsDisposed, "Scoped IDisposable service should be disposed after session.Dispose()");
    }

    /// <summary>
    /// After session.DisposeAsync(), all scoped IDisposable services are released.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ReleasesScopedDisposableServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<TranscriptWorkspaceContext>();
        services.AddScoped(_ => CreateSafeUninitializedWorkspace());
        services.AddScoped<DisposableTracker>();

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var factory = new TrackdubSessionFactory(serviceProvider);
        string tempDir = CreateTempDirectory();

        var session = factory.CreateSession(tempDir);
        DisposableTracker tracker = GetScopedService<DisposableTracker>(session);
        Assert.False(tracker.IsDisposed);

        // Act
        await session.DisposeAsync();

        // Assert
        Assert.True(tracker.IsDisposed, "Scoped IDisposable service should be disposed after session.DisposeAsync()");
    }

    /// <summary>
    /// After session.DisposeAsync(), scoped IAsyncDisposable services are released.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ReleasesAsyncDisposableServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<TranscriptWorkspaceContext>();
        services.AddScoped(_ => CreateSafeUninitializedWorkspace());
        services.AddScoped<AsyncDisposableTracker>();

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var factory = new TrackdubSessionFactory(serviceProvider);
        string tempDir = CreateTempDirectory();

        var session = factory.CreateSession(tempDir);
        AsyncDisposableTracker tracker = GetScopedService<AsyncDisposableTracker>(session);
        Assert.False(tracker.IsDisposed);

        // Act
        await session.DisposeAsync();

        // Assert
        Assert.True(tracker.IsDisposed, "Scoped IAsyncDisposable service should be disposed after session.DisposeAsync()");
    }

    /// <summary>
    /// Disposing a session multiple times does not throw (idempotent disposal).
    /// </summary>
    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<TranscriptWorkspaceContext>();
        services.AddScoped(_ => CreateSafeUninitializedWorkspace());
        services.AddScoped<DisposableTracker>();

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var factory = new TrackdubSessionFactory(serviceProvider);
        string tempDir = CreateTempDirectory();

        var session = factory.CreateSession(tempDir);
        DisposableTracker tracker = GetScopedService<DisposableTracker>(session);

        // Act & Assert — no exception on repeated disposal
        session.Dispose();
        var exception = Record.Exception(() => session.Dispose());

        Assert.Null(exception);
        Assert.True(tracker.IsDisposed);
    }

    /// <summary>
    /// DisposeAsync multiple times does not throw (idempotent disposal).
    /// </summary>
    [Fact]
    public async Task DisposeAsync_MultipleTimes_DoesNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<TranscriptWorkspaceContext>();
        services.AddScoped(_ => CreateSafeUninitializedWorkspace());
        services.AddScoped<DisposableTracker>();

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var factory = new TrackdubSessionFactory(serviceProvider);
        string tempDir = CreateTempDirectory();

        var session = factory.CreateSession(tempDir);
        DisposableTracker tracker = GetScopedService<DisposableTracker>(session);

        // Act & Assert — no exception on repeated async disposal
        await session.DisposeAsync();
        var exception = await Record.ExceptionAsync(async () => await session.DisposeAsync());

        Assert.Null(exception);
        Assert.True(tracker.IsDisposed);
    }

    /// <summary>
    /// Multiple scoped disposable services are all released on session disposal.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesMultipleScopedServices()
    {
        // Arrange: register two distinct IDisposable services via different interfaces.
        var services = new ServiceCollection();
        services.AddScoped<TranscriptWorkspaceContext>();
        services.AddScoped(_ => CreateSafeUninitializedWorkspace());
        services.AddScoped<DisposableTracker>();
        services.AddScoped<SecondDisposableTracker>();

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var factory = new TrackdubSessionFactory(serviceProvider);
        string tempDir = CreateTempDirectory();

        var session = factory.CreateSession(tempDir);
        DisposableTracker tracker1 = GetScopedService<DisposableTracker>(session);
        SecondDisposableTracker tracker2 = GetScopedService<SecondDisposableTracker>(session);

        // Act
        session.Dispose();

        // Assert
        Assert.True(tracker1.IsDisposed, "First scoped IDisposable service should be released");
        Assert.True(tracker2.IsDisposed, "Second scoped IDisposable service should be released");
    }

    /// <summary>
    /// Resolves a scoped service from the session's scoped provider to get the
    /// container-owned instance to verify disposal.
    /// </summary>
    private static T GetScopedService<T>(TrackdubSession session) where T : notnull =>
        session.Services.GetRequiredService<T>();

    /// <summary>
    /// Creates a TranscriptWorkspace instance that is safe to hold without requiring
    /// the full dependency graph. Uses GetUninitializedObject and then initializes
    /// the fields required for safe disposal via TrackdubSession.
    /// </summary>
    private static TranscriptWorkspace CreateSafeUninitializedWorkspace()
    {
        var workspace = (TranscriptWorkspace)RuntimeHelpers.GetUninitializedObject(typeof(TranscriptWorkspace));

        Type type = typeof(TranscriptWorkspace);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        type.GetField("disposalSync", flags)?.SetValue(workspace, new object());
        type.GetField("_pipelineGuard", flags)?.SetValue(workspace, new SemaphoreSlim(1, 1));
        type.GetField("workspaceCancellation", flags)?.SetValue(workspace, new CancellationTokenSource());
        type.GetField("disposed", flags)?.SetValue(workspace, true); // Mark as already disposed to skip Dispose logic.

        return workspace;
    }

    private string CreateTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
