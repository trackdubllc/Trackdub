using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Trackdub.Sdk;

/// <summary>
/// File-based project directory lock that prevents concurrent runs targeting the same project.
/// Uses an exclusive <see cref="FileStream"/> on a <c>.trackdub.lock</c> file to detect conflicts.
/// Stale locks from crashed processes are automatically reclaimed, and lock identity follows
/// symlinks (and case on case-insensitive file systems) so that two paths naming the same
/// project conflict.
/// </summary>
public sealed class ProjectLock : IDisposable, IAsyncDisposable
{
    private const string LockFileName = ".trackdub.lock";

    /// <summary>
    /// How long a lock file without a readable PID is treated as still being written by a live
    /// holder that has not flushed its PID yet. Comfortably longer than the create-then-write
    /// window it guards, and short enough that a crashed run's leftover file clears quickly.
    /// </summary>
    private static readonly TimeSpan LockFileSettleDelay = TimeSpan.FromSeconds(2);

    private static readonly object s_globalLock = new();

    /// <summary>
    /// In-process registry of currently held lock paths (canonical, and case-folded on
    /// case-insensitive file systems).  On Linux, <c>FileShare.None</c> is not
    /// enforced for intra-process opens, so we need this secondary guard to stop the
    /// same process from acquiring the same directory lock twice.
    /// </summary>
    private static readonly HashSet<string> s_heldPaths = new(StringComparer.Ordinal);

    private readonly string _lockFilePath;
    private readonly string _registryKey;
    private FileStream? _lockStream;
    private volatile bool _disposed;

    private ProjectLock(string lockFilePath, string registryKey, FileStream lockStream)
    {
        _lockFilePath = lockFilePath;
        _registryKey = registryKey;
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
        string canonicalPath = CanonicalizeDirectoryPath(fullPath);
        string lockFilePath = Path.Join(canonicalPath, LockFileName);
        string registryKey = BuildRegistryKey(canonicalPath);

        // Ensure the directory exists so we can create the lock file.
        Directory.CreateDirectory(canonicalPath);

        // Serialize lock acquisition within this process to prevent races
        // between threads trying to lock the same directory.
        lock (s_globalLock)
        {
            // Intra-process guard: on Linux FileShare.None is not enforced between
            // FileStream instances within the same process, so we maintain our own set.
            if (s_heldPaths.Contains(registryKey))
            {
                throw new ProjectLockedException(projectDirectory, Environment.ProcessId);
            }

            // Attempt to open the lock file with exclusive access.
            FileStream? stream = TryOpenExclusive(lockFilePath);

            if (stream is null)
            {
                // Lock file is held by another process. Check if it's stale.
                LockFileRead read = TryReadHoldingProcessId(lockFilePath);

                if (read.ReadFailed)
                {
                    // The holder is unknown. Do not unlink a possibly-live lock: on Unix
                    // unlink succeeds on a file another process holds open, so deleting it
                    // would let this run acquire a replacement lock alongside the existing run.
                    throw new ProjectLockedException(projectDirectory);
                }

                if (read.HoldingPid is { } holdingPid && IsProcessAlive(holdingPid))
                {
                    // The holding process is still running — genuine conflict.
                    throw new ProjectLockedException(projectDirectory, holdingPid);
                }

                // Stale lock (holder gone) or holderless file (crash between open and write).
                // A holderless file that was written moments ago is more likely a live holder
                // that has not flushed its PID yet than a crashed run — the lock file is
                // created by the exclusive open and only then filled in — so fail closed until
                // the file has settled.
                if (read.HoldingPid is null && IsStillBeingWritten(lockFilePath))
                {
                    throw new ProjectLockedException(projectDirectory);
                }

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

            s_heldPaths.Add(registryKey);
            return new ProjectLock(lockFilePath, registryKey, stream);
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
            s_heldPaths.Remove(_registryKey);
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
    /// Resolves a path to its physical form, following symlinks in every component, so that two
    /// paths naming the same directory through different links produce the same identity for the
    /// in-process registry. On Windows the OS enforces <see cref="FileShare.None"/> between
    /// <see cref="FileStream"/> instances in one process, so the registry key only needs the
    /// lexical normalization that <see cref="Path.GetFullPath"/> already provides.
    /// </summary>
    private static string CanonicalizeDirectoryPath(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        string root = Path.GetPathRoot(fullPath) ?? Path.DirectorySeparatorChar.ToString();

        // Path.GetFullPath preserves a trailing separator. Left in place it makes the first
        // component below empty, the walk stops before collecting anything, and the whole
        // path collapses to the root — so the lock would be placed in the file system root
        // instead of the project directory. Trim it, keeping the root itself intact.
        string trimmed = fullPath;
        while (trimmed.Length > root.Length && trimmed[^1] == Path.DirectorySeparatorChar)
        {
            trimmed = trimmed[..^1];
        }

        // Collect the components beneath the root so each can be checked for being a symlink.
        var parts = new List<string>();
        string remaining = trimmed;
        while (remaining.Length > root.Length)
        {
            string name = Path.GetFileName(remaining);
            if (name.Length == 0)
            {
                break;
            }

            parts.Insert(0, name);
            remaining = Path.GetDirectoryName(remaining) ?? root;
        }

        string resolved = root;
        foreach (string part in parts)
        {
            resolved = Path.Join(resolved, part);
            if (Directory.Exists(resolved) &&
                new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                // Resolve links in the target's ancestors as well. A link target may use
                // another alias for the same filesystem path (for example /var vs
                // /private/var on macOS), otherwise two spellings can still get different
                // registry keys after this component is followed.
                resolved = CanonicalizeDirectoryPath(target.FullName);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Builds the in-process registry key for a canonical project path. Symlink resolution
    /// alone leaves case unnormalized, so on a case-insensitive file system — where the OS
    /// file lock is advisory and this registry is the only intra-process guard — two paths
    /// differing only in case would name one project under two keys. Case-fold there and
    /// leave case-sensitive volumes untouched, where <c>Foo</c> and <c>foo</c> really are
    /// two directories and folding would report a false conflict.
    /// </summary>
    private static string BuildRegistryKey(string canonicalPath) =>
        BuildRegistryKey(canonicalPath, FileSystemIsCaseInsensitive(canonicalPath));

    /// <summary>
    /// Key-building half of <see cref="BuildRegistryKey(string)"/>, taking the case-sensitivity
    /// answer from the caller so both volume kinds can be exercised on any host.
    /// </summary>
    internal static string BuildRegistryKey(string canonicalPath, bool caseInsensitiveFileSystem) =>
        caseInsensitiveFileSystem
            ? Path.Join(canonicalPath, LockFileName).ToUpperInvariant()
            : Path.Join(canonicalPath, LockFileName);

    /// <summary>
    /// Reports whether upper-casing a name still reaches the same entry, which is exactly the
    /// question the registry key depends on. Probed on the nearest existing ancestor, so a
    /// project directory that does not exist yet is never created just to answer the question.
    /// The probe creates one uniquely named entry there and asks the file system for that same
    /// entry under an upper-cased spelling: mere existence of the upper-cased spelling would also
    /// be answered by a case-variant *sibling* on a case-sensitive volume, which would report
    /// such a volume as case-insensitive and fold two distinct projects onto one registry key.
    /// A volume that cannot be probed reports false, which keeps the previous behaviour rather
    /// than guessing.
    /// </summary>
    internal static bool FileSystemIsCaseInsensitive(string directoryPath)
    {
        DirectoryInfo? probe = new(directoryPath);
        while (probe is { Exists: false })
        {
            probe = probe.Parent;
        }

        if (probe is null)
        {
            return false;
        }

        string probeName = $".trackdub-case-probe-{Guid.NewGuid():N}";
        string probeFile = Path.Join(probe.FullName, probeName);

        try
        {
            File.WriteAllText(probeFile, string.Empty);

            return File.Exists(Path.Join(probe.FullName, probeName.ToUpperInvariant()));
        }
        catch (IOException)
        {
            // Cannot create the probe here, so the question stays unanswered.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probeFile);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leftover probe file is inert and uniquely named.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; a leftover probe file is inert and uniquely named.
            }
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
    /// Distinguishes "the file could not be read at all" from "the file is readable but carries
    /// no PID" because only the former must block stale-lock reclamation.
    /// </summary>
    private static LockFileRead TryReadHoldingProcessId(string lockFilePath)
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
            {
                // Holderless lock file (for example a crash between open and write).
                return new LockFileRead(ReadFailed: false, HoldingPid: null);
            }

            // Parse the JSON lock info.
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("pid", out JsonElement pidElement) &&
                pidElement.TryGetInt32(out int pid))
            {
                return new LockFileRead(ReadFailed: false, HoldingPid: pid);
            }

            return new LockFileRead(ReadFailed: false, HoldingPid: null);
        }
        catch (JsonException)
        {
            // The file is readable but its contents are not lock info — for example a crash
            // left partial JSON. There is no PID to act on and nothing was held that we can
            // observe, so keep it reclaimable rather than blocking the project until someone
            // deletes the lock file by hand.
            return new LockFileRead(ReadFailed: false, HoldingPid: null);
        }
        catch (FileNotFoundException)
        {
            // The holder released and deleted the lock file between our failed exclusive open
            // and this read, so there is nothing left to protect.
            return new LockFileRead(ReadFailed: false, HoldingPid: null);
        }
        catch (DirectoryNotFoundException)
        {
            // The lock file's directory is gone, so there is no live holder either.
            return new LockFileRead(ReadFailed: false, HoldingPid: null);
        }
        catch
        {
            // The file could not be read, so the holder is unknown. This must not be treated
            // as stale: on Unix unlink succeeds on a file another process holds open, so
            // reclaiming here would let a second run overwrite a live lock.
            return new LockFileRead(ReadFailed: true, HoldingPid: null);
        }
    }

    /// <summary>
    /// Reports whether the lock file was written so recently that its missing or unreadable
    /// content is better explained by a holder that has not written its PID yet than by a
    /// crashed run. Age is the only signal that tells the two apart, and being wrong in the
    /// reclaiming direction unlinks a live holder's file, so an unreadable timestamp also
    /// reports true.
    /// </summary>
    private static bool IsStillBeingWritten(string lockFilePath)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(lockFilePath) < LockFileSettleDelay;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
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
    /// Result of reading a lock file's holder PID. <see cref="ReadFailed"/> is set when the file
    /// could not be read at all (the holder is unknown); a readable file without a PID — such as
    /// one left empty by a crash between open and write — reports <c>ReadFailed = false</c> with a
    /// null PID and remains reclaimable.
    /// </summary>
    private readonly record struct LockFileRead(bool ReadFailed, int? HoldingPid);

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
