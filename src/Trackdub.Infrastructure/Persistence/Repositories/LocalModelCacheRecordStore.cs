using System.Text;
using System.Text.Json;
using Trackdub.Domain;
using Trackdub.Infrastructure;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class LocalModelCacheRecordStore(TrackdubStoragePaths storagePaths)
{
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    /// <summary>
    /// Atomically reads the current cache index, applies <paramref name="mutator"/>, and persists the result.
    /// This is the only thread-safe way to update the index: concurrent calls are serialized through an
    /// internal lock so read-modify-write sequences cannot lose updates. All production mutations must go
    /// through this method.
    /// </summary>
    public async Task MutateAsync(
        Func<IReadOnlyList<LocalModelCacheRecord>, IReadOnlyList<LocalModelCacheRecord>> mutator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<LocalModelCacheRecord> current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            await SaveAsync(mutator(current), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    /// <summary>
    /// Reads the cache index. Safe for concurrent readers; does not acquire the mutation lock.
    /// Callers that modify and persist must use <see cref="MutateAsync"/> instead.
    /// </summary>
    /// <remarks>
    /// The index is small and always read whole, so it is read into a buffer synchronously and
    /// deserialized from memory, matching the synchronous reads in <see cref="FileSmokeVerdictStore"/>
    /// and <see cref="TrackdubStoragePathResolver"/>. Measured on a two-record index, this removes
    /// ~9 ms of the one-time first read and roughly halves the per-read cost after it (0.30 ms →
    /// 0.16 ms) that the async reader spent on its own machinery; the dominant cost is still
    /// building the serializer metadata for the record graph, which this does not change.
    /// <para>
    /// A leading UTF-8 byte order mark is skipped, so an index written by an editor or script that
    /// emits one (Notepad, PowerShell <c>-Encoding utf8</c>) loads exactly as it did through the
    /// stream-based read this replaced. The store never writes a mark itself.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(storagePaths.ModelCacheIndexPath))
        {
            return [];
        }

        byte[] payload = File.ReadAllBytes(storagePaths.ModelCacheIndexPath);
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> json = payload;
        ReadOnlySpan<byte> preamble = Encoding.UTF8.GetPreamble();
        if (json.StartsWith(preamble))
        {
            json = json[preamble.Length..];
        }

        LocalModelCacheRecord[]? records = JsonSerializer.Deserialize(
            json,
            LocalModelCacheSerializationContext.Default.LocalModelCacheRecordArray);

        return records ?? [];
    }

    /// <summary>
    /// Persists the cache index via temp-file write and replace. Not thread-safe by itself; production code
    /// must route mutations through <see cref="MutateAsync"/>. Internal so tests can seed initial state.
    /// </summary>
    internal async Task SaveAsync(
        IReadOnlyList<LocalModelCacheRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        Directory.CreateDirectory(storagePaths.ModelCacheDirectory);
        string indexPath = storagePaths.ModelCacheIndexPath;
        string tempPath = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.Asynchronous))
            {
                // The generated metadata is bound to LocalModelCacheRecord[], so materialize
                // IReadOnlyList<T> implementations (e.g. single-element wrappers) before serializing.
                LocalModelCacheRecord[] materialized = records is LocalModelCacheRecord[] array
                    ? array
                    : [.. records];
                await JsonSerializer.SerializeAsync(stream, materialized, LocalModelCacheSerializationContext.Default.LocalModelCacheRecordArray, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, indexPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
