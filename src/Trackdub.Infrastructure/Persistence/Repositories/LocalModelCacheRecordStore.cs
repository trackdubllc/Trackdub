using System.Text.Json;
using Trackdub.Domain;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class LocalModelCacheRecordStore(TrackdubStoragePaths storagePaths)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

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
    public async Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(storagePaths.ModelCacheIndexPath))
        {
            return [];
        }

        await using var stream = new FileStream(
            storagePaths.ModelCacheIndexPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);
        LocalModelCacheRecord[]? records = await JsonSerializer.DeserializeAsync<LocalModelCacheRecord[]>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

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
                await JsonSerializer.SerializeAsync(stream, records, SerializerOptions, cancellationToken)
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
