using Trackdub.Application.Transcripts;
using Trackdub.Sdk.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Integration tests for the SDK session lifecycle using the real <see cref="HeadlessCompositionRoot"/>.
/// Verifies that the full DI wiring produces functional sessions with accessible workspaces.
///
/// **Validates: Requirements 4.1, 14.1, 14.2, 14.4**
/// </summary>
public sealed class SdkSessionLifecycleIntegrationTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    /// <summary>
    /// Build a factory with default options, create a session with a temp directory,
    /// verify session.Workspace is not null and session.ProjectRootPath matches.
    ///
    /// **Validates: Requirements 4.1, 14.1**
    /// </summary>
    [Fact]
    public void BuildFactory_CreateSession_WorkspaceIsAccessible()
    {
        // Arrange
        using var factory = CreateFactory();
        string projectDir = CreateTempDirectory();

        // Act
        using var session = factory.CreateSession(projectDir);

        // Assert
        Assert.NotNull(session.Workspace);
        Assert.Equal(
            Path.GetFullPath(projectDir),
            session.ProjectRootPath,
            ignoreCase: true);
    }

    /// <summary>
    /// Create multiple sessions concurrently on different project directories,
    /// verify each has independent state (distinct workspace instances and correct paths).
    ///
    /// **Validates: Requirements 14.1, 14.2**
    /// </summary>
    [Fact]
    public async Task ConcurrentSessions_DifferentDirectories_DoNotInterfere()
    {
        // Arrange
        using var factory = CreateFactory();
        const int sessionCount = 5;
        string[] projectDirs = Enumerable.Range(0, sessionCount)
            .Select(_ => CreateTempDirectory())
            .ToArray();

        // Act: create sessions concurrently
        Task<TrackdubSession>[] tasks = projectDirs
            .Select(dir => Task.Run(() => factory.CreateSession(dir)))
            .ToArray();

        TrackdubSession[] sessions = await Task.WhenAll(tasks);

        try
        {
            // Assert: all sessions created successfully
            Assert.Equal(sessionCount, sessions.Length);
            Assert.All(sessions, s => Assert.NotNull(s));

            // Assert: each session has a distinct ProjectRootPath
            string[] paths = sessions.Select(s => s.ProjectRootPath).ToArray();
            Assert.Equal(sessionCount, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // Assert: each session has a distinct Workspace instance
            for (int i = 0; i < sessions.Length; i++)
            {
                Assert.NotNull(sessions[i].Workspace);
                for (int j = i + 1; j < sessions.Length; j++)
                {
                    Assert.NotSame(sessions[i].Workspace, sessions[j].Workspace);
                }
            }

            // Assert: each session's ProjectRootPath matches the directory it was created with
            for (int i = 0; i < sessions.Length; i++)
            {
                Assert.Equal(
                    Path.GetFullPath(projectDirs[i]),
                    sessions[i].ProjectRootPath,
                    ignoreCase: true);
            }
        }
        finally
        {
            foreach (var session in sessions)
            {
                session.Dispose();
            }
        }
    }

    /// <summary>
    /// Create a session, dispose it, verify no exceptions and the factory can still create new sessions.
    ///
    /// **Validates: Requirements 14.4**
    /// </summary>
    [Fact]
    public void SessionDisposal_ReleasesResources_FactoryRemainsUsable()
    {
        // Arrange
        using var factory = CreateFactory();
        string firstDir = CreateTempDirectory();
        string secondDir = CreateTempDirectory();

        // Act: create and dispose first session
        var firstSession = factory.CreateSession(firstDir);
        Assert.NotNull(firstSession.Workspace);

        var disposeException = Record.Exception(() => firstSession.Dispose());

        // Assert: disposal did not throw
        Assert.Null(disposeException);

        // Act: create a second session from the same factory after first was disposed
        using var secondSession = factory.CreateSession(secondDir);

        // Assert: second session is fully functional
        Assert.NotNull(secondSession.Workspace);
        Assert.Equal(
            Path.GetFullPath(secondDir),
            secondSession.ProjectRootPath,
            ignoreCase: true);
    }

    /// <summary>
    /// Async disposal of a session releases resources and the factory remains usable.
    ///
    /// **Validates: Requirements 14.4**
    /// </summary>
    [Fact]
    public async Task SessionDisposalAsync_ReleasesResources_FactoryRemainsUsable()
    {
        // Arrange
        using var factory = CreateFactory();
        string firstDir = CreateTempDirectory();
        string secondDir = CreateTempDirectory();

        // Act: create and async-dispose first session
        var firstSession = factory.CreateSession(firstDir);
        Assert.NotNull(firstSession.Workspace);

        var disposeException = await Record.ExceptionAsync(async () => await firstSession.DisposeAsync());

        // Assert: async disposal did not throw
        Assert.Null(disposeException);

        // Act: create a second session from the same factory
        using var secondSession = factory.CreateSession(secondDir);

        // Assert: second session is fully functional
        Assert.NotNull(secondSession.Workspace);
        Assert.Equal(
            Path.GetFullPath(secondDir),
            secondSession.ProjectRootPath,
            ignoreCase: true);
    }

    /// <summary>
    /// Creates a <see cref="TrackdubSessionFactory"/> using the real
    /// <see cref="HeadlessCompositionRoot"/> with default options.
    /// </summary>
    private TrackdubSessionFactory CreateFactory()
    {
        var options = new TrackdubOptions();
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub(options);
        var provider = services.BuildServiceProvider();
        return new TrackdubSessionFactory(provider);
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
