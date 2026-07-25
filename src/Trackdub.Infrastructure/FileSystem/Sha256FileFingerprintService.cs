using System.Collections.Concurrent;
using System.Security.Cryptography;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Logging;

namespace Trackdub.Infrastructure.FileSystem;

public sealed class Sha256FileFingerprintService(IApplicationLogger? logger = null) : IFileFingerprintService
{
    internal const int MaxCachedFingerprints = 512;

    private readonly object cacheTrimLock = new();
    private readonly IApplicationLogger logger = logger ?? new DebugApplicationLogger();
    private readonly ConcurrentDictionary<string, FingerprintCacheEntry> fingerprintCache = new(StringComparer.OrdinalIgnoreCase);
    private long cacheAccessSequence;

    public async Task<FileFingerprint> ComputeAsync(string path, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("File was not found for fingerprinting.", fullPath);
        }

        var fileInfo = new FileInfo(fullPath);

        if (fingerprintCache.TryGetValue(fullPath, out var cached) &&
            cached.Size == fileInfo.Length &&
            cached.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc)
        {
            TouchCachedFingerprint(fullPath, cached);
            logger.LogDebug($"Fingerprint cache hit for file: '{fullPath}'");
            return new FileFingerprint(cached.Hash, cached.Size, cached.LastWriteTimeUtc);
        }

        logger.LogDebug($"Computing fingerprint for file: '{fullPath}'");

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        string hashHex = Convert.ToHexString(hash).ToLowerInvariant();

        // Align returned fingerprint and cache entry with the bytes we hashed (post-read metadata).
        fileInfo.Refresh();
        logger.LogDebug($"Fingerprint computed: SHA256={hashHex} Size={fileInfo.Length} bytes");

        CacheFingerprint(fullPath, fileInfo.Length, fileInfo.LastWriteTimeUtc, hashHex);

        return new FileFingerprint(hashHex, fileInfo.Length, fileInfo.LastWriteTimeUtc);
    }

    internal int CachedFingerprintCountForTesting => fingerprintCache.Count;

    internal bool ContainsCachedFingerprintForTesting(string path) =>
        fingerprintCache.ContainsKey(Path.GetFullPath(path));

    private long NextCacheAccessSequence() =>
        Interlocked.Increment(ref cacheAccessSequence);

    private void TouchCachedFingerprint(string fullPath, FingerprintCacheEntry cached)
    {
        var updated = cached with { LastAccessSequence = NextCacheAccessSequence() };
        fingerprintCache.TryUpdate(fullPath, updated, cached);
    }

    private void CacheFingerprint(string fullPath, long size, DateTime lastWriteTimeUtc, string hash)
    {
        var entry = new FingerprintCacheEntry(size, lastWriteTimeUtc, hash, NextCacheAccessSequence());
        lock (cacheTrimLock)
        {
            if (!fingerprintCache.ContainsKey(fullPath))
            {
                EvictLeastRecentlyUsedEntries();
            }

            fingerprintCache[fullPath] = entry;
        }
    }

    private void EvictLeastRecentlyUsedEntries()
    {
        while (fingerprintCache.Count >= MaxCachedFingerprints && TryFindLeastRecentlyUsedEntry(out string? path))
        {
            fingerprintCache.TryRemove(path, out _);
        }
    }

    private bool TryFindLeastRecentlyUsedEntry([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? path)
    {
        path = null;
        long oldestAccess = long.MaxValue;
        foreach ((string candidatePath, FingerprintCacheEntry candidate) in fingerprintCache)
        {
            if (candidate.LastAccessSequence >= oldestAccess)
            {
                continue;
            }

            oldestAccess = candidate.LastAccessSequence;
            path = candidatePath;
        }

        return path is not null;
    }

    private sealed record FingerprintCacheEntry(
        long Size,
        DateTime LastWriteTimeUtc,
        string Hash,
        long LastAccessSequence);
}
