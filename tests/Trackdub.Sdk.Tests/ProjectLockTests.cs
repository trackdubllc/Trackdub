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

    [Fact]
    public void Acquire_DirectoryWithTrailingSeparator_LocksTheProjectDirectory()
    {
        // Arrange — a caller-supplied output directory can arrive with a trailing separator.
        string dir = CreateTempDirectory();

        // Act
        using var lockHandle = ProjectLock.Acquire(dir + Path.DirectorySeparatorChar);

        // Assert — the lock file lands in the project directory, not in the file system root.
        Assert.True(File.Exists(Path.Join(dir, ".trackdub.lock")));
    }

    [UnixOnlyFact]
    public void Acquire_EmptyLockFileHeldByAnotherHandle_IsReclaimable()
    {
        // Arrange — an empty lock file left behind by a crash between open and write must
        // not permanently block acquisition. The file has to be *held*, otherwise the
        // exclusive open in Acquire succeeds and the holderless branch is never reached.
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");
        File.WriteAllText(lockPath, string.Empty);

        using FileStream holder = new(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead);

        // The premise of the test: an exclusive open must be refused. If it is not, the
        // branch under test is skipped and this test would pass against any code.
        Assert.False(CanOpenExclusively(lockPath));

        // Act
        using var lockHandle = ProjectLock.Acquire(dir);

        // Assert
        Assert.NotNull(lockHandle);
    }

    [UnixOnlyFact]
    public void Acquire_UnreadableLockFile_DoesNotDeleteAndFailsClosed()
    {
        // Arrange — a lock file whose content cannot be read (mode 000).
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");
        File.WriteAllText(lockPath, """{"pid":2147483647,"timestamp":"2024-01-01T00:00:00Z","machineName":"STALE"}""");
        File.SetUnixFileMode(lockPath, UnixFileMode.None);

        // If the environment can still read the file (for example running as root), the
        // premise of the test — an unreadable lock file — does not hold, so there is
        // nothing to assert.
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (probe.CanRead)
            {
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Expected — the file is unreadable; proceed with the assertion.
        }
        catch (IOException)
        {
            // Expected — the file is unreadable; proceed with the assertion.
        }

        // Act — the holder is unknown, so it must fail closed instead of unlinking the
        // lock (on Unix unlink succeeds on a file another process holds open) and running
        // alongside the existing holder.
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(dir));
        Assert.Null(ex.HoldingProcessId);

        // Assert — the lock file was not deleted.
        Assert.True(File.Exists(lockPath));
    }

    [UnixOnlyFact]
    public void Acquire_SamePhysicalDirectory_ViaSymlink_ThrowsProjectLockedException()
    {
        // Arrange — a symlink naming the same directory as the real path.
        string parent = CreateTempDirectory();
        string realProject = Path.Join(parent, "real");
        Directory.CreateDirectory(realProject);
        string link = Path.Join(parent, "alias");
        Directory.CreateSymbolicLink(link, realProject);

        using var firstLock = ProjectLock.Acquire(realProject);

        // Act & Assert — the symlink path resolves to the same lock identity, so the
        // in-process registry reports a conflict rather than allowing a second run.
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(link));
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Equal(Environment.ProcessId, ex.HoldingProcessId);
    }

    [UnixOnlyFact]
    public void Acquire_SamePhysicalDirectory_ViaSymlinkedAncestor_ThrowsProjectLockedException()
    {
        // Arrange — a symlinked ancestor: "/parent/alias/proj" names the same directory
        // as "/parent/real/proj".
        string parent = CreateTempDirectory();
        string realRoot = Path.Join(parent, "real");
        string realProject = Path.Join(realRoot, "proj");
        Directory.CreateDirectory(realProject);
        string linkRoot = Path.Join(parent, "alias");
        Directory.CreateSymbolicLink(linkRoot, realRoot);

        using var firstLock = ProjectLock.Acquire(realProject);

        // Act & Assert
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(Path.Join(linkRoot, "proj")));
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Equal(Environment.ProcessId, ex.HoldingProcessId);
    }

    [UnixOnlyFact]
    public void Dispose_ReleaseViaRealPath_AllowsAcquireViaSymlink()
    {
        // Arrange — a symlink naming the same directory as the real path.
        string parent = CreateTempDirectory();
        string realProject = Path.Join(parent, "real");
        Directory.CreateDirectory(realProject);
        string link = Path.Join(parent, "alias");
        Directory.CreateSymbolicLink(link, realProject);

        // Act — release the lock acquired through the real path...
        var firstLock = ProjectLock.Acquire(realProject);
        firstLock.Dispose();

        // Assert — ...then re-acquire through the symlink succeeds because both resolve
        // to the same registry key.
        using var secondLock = ProjectLock.Acquire(link);
        Assert.NotNull(secondLock);
    }

    /// <summary>
    /// Reports whether the lock file can be opened the way <see cref="ProjectLock"/> opens
    /// it. Used to prove that a test's premise — the exclusive open fails — actually holds
    /// on this platform, so the branch under test is reached.
    /// </summary>
    private static bool CanOpenExclusively(string lockPath)
    {
        try
        {
            using FileStream probe = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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

/// <summary>
/// Marks a test whose reproduction depends on Unix file, permission and symlink semantics.
/// Reports as skipped on Windows instead of returning early and reporting as passed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix-only test: the reproduction depends on Unix file, permission and symlink semantics.";
        }
    }
}
