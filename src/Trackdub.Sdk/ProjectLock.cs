using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Trackdub.Sdk;

/// <summary>
/// File-based project directory lock that prevents concurrent runs targeting the same project.
/// Uses an exclusive <see cref="FileStream"/> on a <c>.trackdub.lock</c> file to detect conflicts.
/// Stale locks from crashed processes are automatically reclaimed.
/// </summary>
public sealed class ProjectLock : IDisposable, IAsyncDisposable
{
    private const string LockFileName = ".trackdub.lock";

    private static readonly object s_globalLock = new();

    /// <summary>
    /// In-process registry of currently held lock paths (canonical, lower-case on
    /// case-insensitive file systems).  On Linux, <c>FileShare.None</c> is not
    /// enforced for intra-process opens, so we need this secondary guard to stop the
    /// same process from acquiring the same directory lock twice.
    /// </summary>
    private static readonly HashSet<string> s_heldPaths = new(StringComparer.Ordinal);

    private readonly string _lockFilePath;
    private FileStream? _lockStream;
    private volatile bool _disposed;

    private ProjectLock(string lockFilePath, FileStream lockStream)
    {
        _lockFilePath = lockFilePath;
        _lockStream = lockStream;
    }

    /// <summary>
    /// Acquires an exclusive lock on the specified project directory.
    /// </summary>
    /// <param name="projectDirectory">The project directory to lock.</param>
    /// <returns>A <see cref="ProjectLock"/> that must be disposed to release the lock.</returns>
    /// <exception cref="ProjectLockedException">
    /// Thrown when the project directory is already locked by another active process.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="projectDirectory"/> is null or whitespace.
    /// </exception>
    public static ProjectLock Acquire(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        string fullPath = Path.GetFullPath(projectDirectory);
        string lockFilePath = Path.Combine(fullPath, LockFileName);

        // Ensure the directory exists so we can create the lock file.
        Directory.CreateDirectory(fullPath);

        // Serialize lock acquisition within this process to prevent races
        // between threads trying to lock the same directory.
        lock (s_globalLock)
        {
            // Intra-process guard: on Linux FileShare.None is not enforced between
            // FileStream instances within the same process, so we maintain our own set.
            if (s_heldPaths.Contains(lockFilePath))
            {
                throw new ProjectLockedException(projectDirectory, Environment.ProcessId);
            }

            // Attempt to open the lock file with exclusive access.
            FileStream? stream = TryOpenExclusive(lockFilePath);

            if (stream is null)
            {
                // Lock file is held by another process. Check if it's stale.
                int? holdingPid = TryReadHoldingProcessId(lockFilePath);

                if (holdingPid.HasValue && IsProcessAlive(holdingPid.Value))
                {
                    // The holding process is still running — genuine conflict.
                    throw new ProjectLockedException(projectDirectory, holdingPid.Value);
                }

                // Stale lock: the process that created it is no longer running.
                // Attempt to delete and re-acquire.
                TryDeleteStaleLockFile(lockFilePath);

                stream = TryOpenExclusive(lockFilePath);
                if (stream is null)
                {
                    // Another process grabbed it between our delete and re-open.
                    throw new ProjectLockedException(projectDirectory);
                }
            }

            // Write diagnostic info (PID + timestamp) to the lock file.
            WriteLockInfo(stream);

            s_heldPaths.Add(lockFilePath);
            return new ProjectLock(lockFilePath, stream);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseLock();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        ReleaseLock();
        return ValueTask.CompletedTask;
    }

    private void ReleaseLock()
    {
        FileStream? stream = Interlocked.Exchange(ref _lockStream, null);
        if (stream is null) return;

        // Remove from the intra-process registry before releasing the file handle,
        // so that a racing Acquire on the same path sees the slot as free only after
        // the file lock is gone.
        lock (s_globalLock)
        {
            s_heldPaths.Remove(_lockFilePath);
        }

        try
        {
            // Dispose alone is sufficient (calls Close). Calling Close then Dispose risks
            // skipping Dispose if Close throws.
            stream.Dispose();
        }
        catch
        {
            // Best-effort close; the OS will release the handle on process exit regardless.
        }

        try
        {
            File.Delete(_lockFilePath);
        }
        catch
        {
            // Best-effort delete; another process may have already removed it,
            // or the directory may have been deleted.
        }
    }

    /// <summary>
    /// Attempts to open the lock file with exclusive access (no sharing).
    /// Returns null if the file is already locked by another process.
    /// </summary>
    private static FileStream? TryOpenExclusive(string lockFilePath)
    {
        try
        {
            return new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 256);
        }
        catch (IOException)
        {
            // File is locked by another process.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Permission denied — treat as locked.
            return null;
        }
    }

    /// <summary>
    /// Attempts to read the PID from an existing lock file (best-effort, non-exclusive read).
    /// </summary>
    private static int? TryReadHoldingProcessId(string lockFilePath)
    {
        try
        {
            // Try to read the file content without exclusive access.
            // This may fail if the file is locked, which is fine.
            using var reader = new FileStream(
                lockFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 256);

            using var sr = new StreamReader(reader);
            string content = sr.ReadToEnd();

            if (string.IsNullOrWhiteSpace(content))
                return null;

            // Parse the JSON lock info.
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("pid", out JsonElement pidElement) &&
                pidElement.TryGetInt32(out int pid))
            {
                return pid;
            }
        }
        catch
        {
            // Any failure reading the lock file — we can't determine the PID.
        }

        return null;
    }

    /// <summary>
    /// Checks whether a process with the given PID is still running.
    /// </summary>
    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Process does not exist.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has exited.
            return false;
        }
    }

    /// <summary>
    /// Attempts to delete a stale lock file. Failures are silently ignored.
    /// </summary>
    private static void TryDeleteStaleLockFile(string lockFilePath)
    {
        try
        {
            File.Delete(lockFilePath);
        }
        catch
        {
            // Best-effort; if we can't delete it, TryOpenExclusive will fail
            // and we'll throw ProjectLockedException.
        }
    }

    /// <summary>
    /// Writes diagnostic information (PID and timestamp) to the lock file.
    /// </summary>
    private static void WriteLockInfo(FileStream stream)
    {
        stream.SetLength(0);
        stream.Position = 0;

        var lockInfo = new LockFileContent
        {
            pid = Environment.ProcessId,
            timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            machineName = Environment.MachineName
        };

        JsonSerializer.Serialize(stream, lockInfo);
        stream.Flush();
    }

    /// <summary>
    /// JSON structure written to the lock file for diagnostics.
    /// </summary>
    private sealed record LockFileContent
    {
        public int pid { get; init; }
        public string timestamp { get; init; } = "";
        public string machineName { get; init; } = "";
    }
}
