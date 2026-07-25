using System.Reflection;
using System.Runtime.CompilerServices;
using Trackdub.Application.Transcripts;
using Trackdub.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// **Validates: Requirements 14.1**
///
/// Property 5: Factory thread safety — concurrent calls to factory.CreateSession(path)
/// each return an independent session without data races.
/// </summary>
public sealed class TrackdubFactoryThreadSafetyTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<TrackdubSession> _sessions = [];
    private readonly List<CancellationTokenSource> _workspaceCancellationSources = [];

    private TrackdubSessionFactory CreateMinimalFactory()
    {
        var services = new ServiceCollection();
        services.AddScoped<TranscriptWorkspaceContext>();
        services.AddScoped(_ => CreateSafeUninitializedWorkspace());

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        return new TrackdubSessionFactory(serviceProvider);
    }

    /// <summary>
    /// Creates a TranscriptWorkspace instance that is safe to hold without requiring
    /// the full dependency graph. Uses GetUninitializedObject and then initializes
    /// the fields required for safe disposal via TrackdubSession.
    /// </summary>
    private TranscriptWorkspace CreateSafeUninitializedWorkspace()
    {
        var workspace = (TranscriptWorkspace)RuntimeHelpers.GetUninitializedObject(typeof(TranscriptWorkspace));

        // Initialize fields required for Dispose() to not throw NullReferenceException.
        Type type = typeof(TranscriptWorkspace);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        var workspaceCancellation = new CancellationTokenSource();
        _workspaceCancellationSources.Add(workspaceCancellation);

        type.GetField("disposalSync", flags)?.SetValue(workspace, new object());
        type.GetField("_pipelineGuard", flags)?.SetValue(workspace, new SemaphoreSlim(1, 1));
        type.GetField("workspaceCancellation", flags)?.SetValue(workspace, workspaceCancellation);
        type.GetField("disposed", flags)?.SetValue(workspace, true); // Mark as already disposed to skip Dispose logic.

        return workspace;
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public async Task ConcurrentCreateSession_ReturnsIndependentSessions(int concurrencyLevel)
    {
        // Arrange
        using var factory = CreateMinimalFactory();
        string[] paths = Enumerable.Range(0, concurrencyLevel)
            .Select(_ => CreateTempDirectory())
            .ToArray();

        // Act: launch N concurrent tasks that each call CreateSession.
        Task<TrackdubSession>[] tasks = paths
            .Select(path => Task.Run(() => factory.CreateSession(path)))
            .ToArray();

        TrackdubSession[] sessions = await Task.WhenAll(tasks);
        TrackSessions(sessions);

        // Assert: all sessions created successfully (no exceptions from Task.WhenAll).
        Assert.Equal(concurrencyLevel, sessions.Length);

        // Assert: all sessions have distinct ProjectRootPath values.
        string[] projectPaths = sessions.Select(s => s.ProjectRootPath).ToArray();
        Assert.Equal(concurrencyLevel, projectPaths.Distinct().Count());

        // Assert: all sessions have distinct Workspace instances (not the same reference).
        TranscriptWorkspace[] workspaces = sessions.Select(s => s.Workspace).ToArray();
        for (int i = 0; i < workspaces.Length; i++)
        {
            for (int j = i + 1; j < workspaces.Length; j++)
            {
                Assert.NotSame(workspaces[i], workspaces[j]);
            }
        }
    }

    [Fact]
    public async Task ConcurrentCreateSession_NoExceptionsUnderContention()
    {
        // Arrange: higher contention scenario with rapid-fire session creation.
        using var factory = CreateMinimalFactory();

        const int concurrencyLevel = 15;
        string[] paths = Enumerable.Range(0, concurrencyLevel)
            .Select(_ => CreateTempDirectory())
            .ToArray();

        // Act: use Parallel.ForEachAsync for maximum thread contention.
        var sessions = new TrackdubSession[concurrencyLevel];
        var exceptions = new List<Exception>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, concurrencyLevel),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (index, _) =>
            {
                try
                {
                    sessions[index] = factory.CreateSession(paths[index]);
                }
                catch (Exception ex)
                {
                    lock (exceptions) { exceptions.Add(ex); }
                }
                return ValueTask.CompletedTask;
            });

        TrackSessions(sessions.Where(s => s is not null).ToArray());

        // Assert: no exceptions occurred during concurrent creation.
        Assert.Empty(exceptions);

        // Assert: all sessions were created.
        Assert.All(sessions, s => Assert.NotNull(s));

        // Assert: each session has a unique ProjectRootPath.
        string[] projectPaths = sessions.Select(s => s.ProjectRootPath).ToArray();
        Assert.Equal(concurrencyLevel, projectPaths.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentCreateSession_SessionsAreIndependentAfterCreation()
    {
        // Arrange: verify that disposing one session does not affect others.
        using var factory = CreateMinimalFactory();

        const int concurrencyLevel = 10;
        string[] paths = Enumerable.Range(0, concurrencyLevel)
            .Select(_ => CreateTempDirectory())
            .ToArray();

        // Act: create sessions concurrently.
        Task<TrackdubSession>[] tasks = paths
            .Select(path => Task.Run(() => factory.CreateSession(path)))
            .ToArray();

        TrackdubSession[] sessions = await Task.WhenAll(tasks);
        TrackSessions(sessions);

        // Dispose half the sessions (workspace is pre-marked disposed, so this is safe).
        for (int i = 0; i < concurrencyLevel / 2; i++)
        {
            sessions[i].Dispose();
        }

        // Assert: remaining sessions still have valid ProjectRootPath and Workspace.
        for (int i = concurrencyLevel / 2; i < concurrencyLevel; i++)
        {
            Assert.False(string.IsNullOrWhiteSpace(sessions[i].ProjectRootPath));
            Assert.NotNull(sessions[i].Workspace);
        }
    }

    private void TrackSessions(TrackdubSession[] sessions)
    {
        _sessions.AddRange(sessions);
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
        foreach (CancellationTokenSource cancellation in _workspaceCancellationSources)
        {
            cancellation.Dispose();
        }

        foreach (TrackdubSession session in _sessions)
        {
            try { session.Dispose(); }
            catch { /* best-effort cleanup — workspace stub may not fully support disposal */ }
        }

        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
