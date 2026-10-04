using System.Globalization;
using System.Text.Json;

namespace Trackdub.Sdk;

/// <summary>
/// File-based project directory lock that prevents concurrent runs targeting the same project.
/// Uses an exclusive <see cref="FileStream"/> on a <c>.trackdub.lock</c> file to detect conflicts.
/// </summary>
/// <remarks>
/// A lock file left behind by a crashed run never blocks a later run: the operating system
/// releases the file when its holder exits, so the next <see cref="Acquire"/> opens it and
/// overwrites the record. A failed exclusive open therefore means a live holder, and is reported
/// as a conflict rather than treated as something to clean up.
/// </remarks>
public sealed class ProjectLock : IDisposable, IAsyncDisposable
{
    private const string LockFileName = ".trackdub.lock";

    private static readonly object s_globalLock = new();

    /// <summary>
    /// In-process registry of currently held lock paths, keyed by the resolved physical path so a
    /// symlink and its target share one entry. The exclusive open in <see cref="Acquire"/> is what
    /// enforces exclusion — <c>FileShare.None</c> is honoured both between processes and between
    /// handles in one process, on Windows and Unix alike — so this registry exists to recognise a
    /// same-process conflict and report our own PID instead of blaming another process.
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
    /// Thrown when the project directory is already locked by another active process or session.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="projectDirectory"/> is null or whitespace.
    /// </exception>
    public static ProjectLock Acquire(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        string fullPath = ResolvePhysicalDirectory(projectDirectory);
        string lockFilePath = Path.Join(fullPath, LockFileName);

        // Ensure the directory exists so we can create the lock file.
        Directory.CreateDirectory(fullPath);

        // Serialize lock acquisition within this process to prevent races
        // between threads trying to lock the same directory.
        lock (s_globalLock)
        {
            if (s_heldPaths.Contains(lockFilePath))
            {
                throw new ProjectLockedException(projectDirectory, Environment.ProcessId);
            }

            FileStream? stream = TryOpenExclusive(lockFilePath);

            if (stream is null)
            {
                // The file is held, so a run is in progress. Deleting it would not stop that run:
                // POSIX unlink succeeds on an open file and only removes the directory entry, so we
                // would create a replacement lock and two runs would write the same project. The
                // delete fails on Windows, which is why this went unnoticed where it was written.
                throw CreateLockedException(projectDirectory, lockFilePath);
            }

            try
            {
                // Write diagnostic info (PID + timestamp) to the lock file.
                WriteLockInfo(stream);
            }
            catch
            {
                // The handle is not registered yet, so nothing else can release it.
                stream.Dispose();
                throw;
            }

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

        // The lock file is deliberately left in place. Deleting it here is unsafe on Unix: the
        // handle above is already gone, so another process can Acquire in the gap and create its
        // own file, which our delete would then unlink out from under that holder. A third run
        // would OpenOrCreate a fresh lock and two runs would write the same project. The leftover
        // is harmless because the next Acquire opens and overwrites it.
    }

    /// <summary>
    /// Resolves the project directory to its physical path so that a symlink and its target produce
    /// one registry key. Only the final component's link chain is resolved — .NET exposes no API
    /// for intermediate components — so a project reached through a symlinked parent keeps that
    /// parent's spelling. Exclusion still holds in that case, because the operating system locks
    /// the file's identity rather than the path used to reach it; only the reported holder degrades
    /// from "this app" to "another process".
    /// </summary>
    private static string ResolvePhysicalDirectory(string projectDirectory)
    {
        string fullPath = Path.GetFullPath(projectDirectory);

        try
        {
            FileSystemInfo? resolved = new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
            return resolved is null ? fullPath : Path.GetFullPath(resolved.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Link resolution is best-effort: it only sharpens the registry key, it is not what
            // enforces exclusion. Falling back to the caller's spelling costs us the in-process
            // guard (the conflict is then blamed on another process), but the file handle in
            // Acquire still excludes the second run, so locking correctness is unaffected.
            return fullPath;
        }
    }

    /// <summary>
    /// Builds the conflict exception, enriched with the holder's PID when that PID can be read.
    /// The read is best-effort: a holder that opened the file with <see cref="FileShare.None"/>
    /// refuses it on both Windows and Unix, so a cross-process conflict normally reports no PID.
    /// </summary>
    private static ProjectLockedException CreateLockedException(string projectDirectory, string lockFilePath) =>
        TryReadHoldingProcessId(lockFilePath) is int holdingProcessId
            ? new ProjectLockedException(projectDirectory, holdingProcessId)
            : new ProjectLockedException(projectDirectory);

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
    /// Returns <see langword="null"/> whenever the content cannot be read or parsed, which is the
    /// normal outcome while a holder has the file open exclusively.
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
