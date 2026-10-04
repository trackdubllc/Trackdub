namespace Trackdub.Sdk.Tests;

/// <summary>
/// Unit tests for <see cref="ProjectLock"/> file-based locking mechanism.
///
/// **Validates: Requirements 14.3**
/// </summary>
public sealed class ProjectLockTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void Acquire_CreatesLockFile_InProjectDirectory()
    {
        // Arrange
        string dir = CreateTempDirectory();

        // Act
        using var lockHandle = ProjectLock.Acquire(dir);

        // Assert — lock file exists (DeleteOnClose means it exists while stream is open)
        string lockPath = Path.Join(dir, ".trackdub.lock");
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public void Acquire_SameDirectory_ThrowsProjectLockedException()
    {
        // Arrange
        string dir = CreateTempDirectory();
        using var firstLock = ProjectLock.Acquire(dir);

        // Act & Assert
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(dir));
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Contains(dir, ex.ProjectDirectory);
    }

    [Fact]
    public void Acquire_DifferentDirectories_BothSucceed()
    {
        // Arrange
        string dir1 = CreateTempDirectory();
        string dir2 = CreateTempDirectory();

        // Act
        using var lock1 = ProjectLock.Acquire(dir1);
        using var lock2 = ProjectLock.Acquire(dir2);

        // Assert — both acquired without exception
        Assert.NotNull(lock1);
        Assert.NotNull(lock2);
    }

    [Fact]
    public void Dispose_ReleasesLock_AllowsReacquisition()
    {
        // Arrange
        string dir = CreateTempDirectory();
        var firstLock = ProjectLock.Acquire(dir);

        // Act — release the lock
        firstLock.Dispose();

        // Assert — can acquire again
        using var secondLock = ProjectLock.Acquire(dir);
        Assert.NotNull(secondLock);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesLock_AllowsReacquisition()
    {
        // Arrange
        string dir = CreateTempDirectory();
        var firstLock = ProjectLock.Acquire(dir);

        // Act — release the lock asynchronously
        await firstLock.DisposeAsync();

        // Assert — can acquire again
        using var secondLock = ProjectLock.Acquire(dir);
        Assert.NotNull(secondLock);
    }

    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        // Arrange
        string dir = CreateTempDirectory();
        var lockHandle = ProjectLock.Acquire(dir);

        // Act & Assert — idempotent disposal
        lockHandle.Dispose();
        var exception = Record.Exception(() => lockHandle.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void Acquire_NullDirectory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectLock.Acquire(null!));
    }

    [Fact]
    public void Acquire_WhitespaceDirectory_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ProjectLock.Acquire("   "));
    }

    [Fact]
    public void Acquire_CreatesDirectoryIfNotExists()
    {
        // Arrange
        string parentDir = CreateTempDirectory();
        string subDir = Path.Join(parentDir, "nested", "project");
        Assert.False(Directory.Exists(subDir));

        // Act
        using var lockHandle = ProjectLock.Acquire(subDir);

        // Assert
        Assert.True(Directory.Exists(subDir));
    }

    [Fact]
    public void Acquire_StaleLockFile_ReclaimsLock()
    {
        // Arrange — simulate a stale lock by writing a lock file with a non-existent PID.
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");

        // Use a PID that almost certainly doesn't exist (max int).
        string staleLockContent = """{"pid":2147483647,"timestamp":"2024-01-01T00:00:00Z","machineName":"STALE"}""";
        File.WriteAllText(lockPath, staleLockContent);

        // Act — should reclaim the stale lock
        using var lockHandle = ProjectLock.Acquire(dir);

        // Assert
        Assert.NotNull(lockHandle);
    }

    [Fact]
    public void Acquire_LeftoverLockFileNamingALiveProcess_IsStillReclaimed()
    {
        // Arrange — a leftover lock file that names a process which is still running.
        // Reclamation depends on the file not being held, never on the recorded PID being dead:
        // the operating system drops the handle when its holder exits, so an unheld file is free.
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");
        File.WriteAllText(lockPath, $$"""{"pid":{{Environment.ProcessId}},"timestamp":"2024-01-01T00:00:00Z","machineName":"LEFTOVER"}""");

        // Act
        using var lockHandle = ProjectLock.Acquire(dir);

        // Assert
        Assert.NotNull(lockHandle);
    }

    [Fact]
    public void Acquire_WhenTheLockFileIsHeld_ThrowsAndLeavesTheHolderIntact()
    {
        // Arrange — a foreign handle holding the lock file exclusively. It is deliberately not
        // registered in ProjectLock's in-process registry, so the code path under test is the one
        // that a real second process takes. FileShare.None is enforced per file rather than per
        // process, so this handle is indistinguishable from an out-of-process holder here.
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");
        const string sentinel = """{"pid":424242,"timestamp":"2024-01-01T00:00:00Z","machineName":"HOLDER"}""";
        File.WriteAllText(lockPath, sentinel);

        using var holder = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 256);

        // Act & Assert — a held lock is a conflict, not something to clean up.
        ProjectLockedException ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(dir));
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);

        // The holder's record must survive: deleting a live lock lets two runs write one project.
        holder.Dispose();
        Assert.Equal(sentinel, File.ReadAllText(lockPath));
    }

    [Fact]
    public void Acquire_ThroughASymlinkToALockedDirectory_ReportsThisProcess()
    {
        // Arrange — a symlink and its target must share one registry entry, otherwise the aliased
        // path misses the in-process guard and the conflict is blamed on another process.
        string real = CreateTempDirectory();
        string link = Path.Join(Path.GetDirectoryName(real)!, Guid.NewGuid().ToString("N") + "-link");

        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // The file system or the account cannot create links; nothing to assert.
        }

        try
        {
            using ProjectLock first = ProjectLock.Acquire(real);

            // Act & Assert
            ProjectLockedException ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(link));
            Assert.Equal(Environment.ProcessId, ex.HoldingProcessId);
        }
        finally
        {
            try { Directory.Delete(link); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Acquire_ConcurrentThreads_OnlyOneSucceeds()
    {
        // Arrange
        string dir = CreateTempDirectory();
        int successCount = 0;
        int failureCount = 0;
        var barrier = new Barrier(4);

        // Act — race 4 threads to acquire the same lock
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            try
            {
                using var lockHandle = ProjectLock.Acquire(dir);
                Interlocked.Increment(ref successCount);
                // Hold the lock briefly
                Thread.Sleep(50);
            }
            catch (ProjectLockedException)
            {
                Interlocked.Increment(ref failureCount);
            }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // Assert — exactly one thread should have acquired the lock
        Assert.Equal(1, successCount);
        Assert.Equal(3, failureCount);
    }

    [Fact]
    public void ProjectLockedException_HasCorrectErrorCode()
    {
        var ex = new ProjectLockedException("/some/path");
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Equal("/some/path", ex.ProjectDirectory);
        Assert.Null(ex.HoldingProcessId);
    }

    [Fact]
    public void ProjectLockedException_WithPid_IncludesPidInMessage()
    {
        var ex = new ProjectLockedException("/some/path", 12345);
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Equal("/some/path", ex.ProjectDirectory);
        Assert.Equal(12345, ex.HoldingProcessId);
        Assert.Contains("12345", ex.Message);
    }

    private string CreateTempDirectory()
    {
        string dir = Path.Join(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
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
