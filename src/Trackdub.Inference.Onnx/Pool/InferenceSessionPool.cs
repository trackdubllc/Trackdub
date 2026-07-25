using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Bounded, LRU-evicting pool of ONNX <see cref="InferenceSession"/> instances shared
/// across engine instances within a process.
/// </summary>
/// <remarks>
/// <para><strong>Lifecycle &amp; lease shape:</strong>
/// Callers obtain an exclusive <see cref="SessionLease"/> via <see cref="GetLeaseAsync"/>.
/// The lease holds the session until disposed; disposal releases the session back to the pool
/// without destroying it.  This allows warm sessions to survive across DI scope boundaries.</para>
///
/// <para><strong>Bounded capacity:</strong>
/// The pool holds at most <c>maxSessions</c> live sessions across all keys (default: 8).
/// This is a <em>count-based</em> cap, not an RSS/working-set-aware limit.
/// When the count limit is reached, the pool evicts the least-recently-released idle session
/// (i.e. a session not currently leased out).
/// If every pooled session is currently leased, the new session is created outside the pool
/// (ephemeral) and disposed when its lease is released.
/// RSS-aware eviction (e.g. triggering on GC pressure or OS committed bytes) is out of scope
/// for this PR and is left as future work.</para>
///
/// <para><strong>Model invalidation:</strong>
/// Session keys are keyed by model file <em>path hash</em>, not file content.  If a model file
/// changes on disk at the same path (e.g. hot-swap during development), evict the stale entry
/// explicitly via <see cref="EvictModelAsync"/> or restart the process.  Content-hash
/// invalidation on every pool lookup is out of scope for this PR.</para>
///
/// <para><strong>Memory pressure guidance:</strong>
/// Each ONNX session can consume hundreds of MB of GPU/CPU memory depending on model size.
/// The default limit of 8 is conservative; reduce it via the constructor when many large
/// models are active simultaneously.  Call <see cref="EvictModelAsync"/> to free memory for
/// a model that is no longer needed, or <see cref="Dispose"/> to release the entire pool.</para>
///
/// <para><strong>Thread safety:</strong>
/// All public APIs are thread-safe.  Sessions are single-threaded: only one caller may hold
/// a lease for a given key at a time, serialised by an internal per-entry gate.</para>
/// </remarks>
internal sealed class InferenceSessionPool : IDisposable
{
    /// <summary>Process-wide shared pool with default settings.</summary>
    /// <remarks>
    /// All production engines (Kokoro, Chatterbox, Whisper, Opus-MT, MADLAD, SepFormer,
    /// Spleeter, SortFormer, Silero-VAD) consume this pool via the
    /// <c>CreatePooled*</c> factory methods in <see cref="OnnxExecutionSessionFactory"/>.
    /// </remarks>
    public static readonly InferenceSessionPool Shared = new();

    private const int DefaultMaxSessions = 8;

    private sealed class PoolEntry(InferenceSession session, bool ephemeral) : IDisposable
    {
        private long lastReleasedTicks = Environment.TickCount64;
        private volatile bool evicted;
        private int disposeState; // 0 = live, 1 = disposed; guarded by Interlocked for idempotency

        public InferenceSession Session { get; } = session;

        /// <summary>Per-entry gate that serialises access (one user at a time).</summary>
        public SemaphoreSlim Gate { get; } = new(0, 1); // Starts unavailable because the creator immediately owns the first lease.

        /// <summary>
        /// When <see langword="true"/> the entry was created beyond the pool limit and will not
        /// be stored; its session is disposed when the lease is released.
        /// </summary>
        public bool Ephemeral { get; private set; } = ephemeral;

        /// <summary>
        /// Heuristic snapshot: <see langword="true"/> when the gate appears idle at the moment of
        /// reading.  <em>Not</em> safe as a disposal guard — a concurrent <see cref="SemaphoreSlim.WaitAsync()"/>
        /// can race between the read and a subsequent dispose.  Eviction paths must use
        /// <c>Gate.Wait(0)</c> to atomically acquire the gate before disposing.
        /// </summary>
        public bool IsIdle => Gate.CurrentCount == 1;

        /// <summary>
        /// <see langword="true"/> once the entry has been removed from the pool by an eviction path.
        /// The lease holder is responsible for disposing the entry when it releases.
        /// </summary>
        public bool IsEvicted => evicted;

        /// <summary>Tick count at last <c>Release()</c> call — used for LRU eviction ordering.</summary>
        public long LastReleasedTicks => Volatile.Read(ref lastReleasedTicks);

        /// <summary>
        /// Marks this entry as evicted so that its lease holder disposes it on release rather than
        /// returning it to the pool.  Called under <c>creationLock</c> by eviction code.
        /// </summary>
        public void MarkEvicted() => evicted = true;

        public void MarkEphemeral() => Ephemeral = true;

        public void Release()
        {
            Volatile.Write(ref lastReleasedTicks, Environment.TickCount64);
            Gate.Release();
        }

        public void ReleaseGateWithoutTouchingLru() => Gate.Release();

        /// <summary>
        /// Idempotent dispose: safe to call from both the eviction path and the lease-release path
        /// concurrently without double-disposing the underlying session or semaphore.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeState, 1) == 0)
            {
                Gate.Dispose();
                Session.Dispose();
            }
        }
    }

    private readonly int maxSessions;
    private readonly ConcurrentDictionary<SessionPoolKey, PoolEntry> entries = new();
    private readonly SemaphoreSlim creationLock = new(1, 1);
    private volatile bool disposed;
    private int pooledCount;
    private int disposeOnce; // 0 = not yet, 1 = disposed; Interlocked guard for single-winner teardown

    public InferenceSessionPool(int maxSessions = DefaultMaxSessions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSessions, 1);
        this.maxSessions = maxSessions;
    }

    /// <summary>
    /// Returns an appropriate default max-session count for the current hardware.
    /// On Windows with a discrete NVIDIA GPU and >= 8 GB VRAM, returns 16.
    /// Otherwise returns the conservative default of 8.
    /// </summary>
    public static int RecommendedMaxSessions(IReadOnlyList<DeviceEntry>? devices)
    {
        if (devices is null)
            return DefaultMaxSessions;

        DeviceEntry? nvidiaGpu = devices.FirstOrDefault(d =>
            d.Kind == DeviceKind.DiscreteGpu &&
            string.Equals(d.VendorName, "NVIDIA", StringComparison.OrdinalIgnoreCase));

        if (nvidiaGpu is not null && nvidiaGpu.DedicatedVramMb >= 8192)
            return 16;

        return DefaultMaxSessions;
    }

    /// <summary>
    /// Pre-warms a session for <paramref name="key"/> without returning a lease.
    /// Useful at startup to amortise first-call latency.
    /// If a session for this key already exists the call is a no-op.
    /// </summary>
    public async Task WarmAsync(
        SessionPoolKey key,
        Func<CancellationToken, Task<InferenceSession>> factory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        using SessionLease lease = await GetLeaseAsync(key, factory, cancellationToken).ConfigureAwait(false);
        // Lease is immediately released on exit — session stays in the pool.
    }

    /// <summary>
    /// Returns an exclusive <see cref="SessionLease"/> for <paramref name="key"/>.
    /// If no session exists for the key, one is created using <paramref name="factory"/>.
    /// If the pool is full and no idle session can be evicted, the new session is created
    /// outside the pool (ephemeral) and disposed when the lease is released.
    /// </summary>
    public async Task<SessionLease> GetLeaseAsync(
        SessionPoolKey key,
        Func<CancellationToken, Task<InferenceSession>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        while (true)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // Fast path: entry already exists — wait on its gate (serialises inference).
            if (entries.TryGetValue(key, out PoolEntry? existing))
            {
                try
                {
                    await existing.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (disposed)
                    {
                        ReleaseLeaseEntry(existing);
                        ObjectDisposedException.ThrowIf(disposed, this);
                    }

                    return BuildLease(existing);
                }
                catch (ObjectDisposedException)
                {
                    // The entry was evicted and its SemaphoreSlim disposed between the dictionary
                    // lookup and WaitAsync. Retry from the top so we can observe the current pool
                    // state (including full-pool disposal) before deciding whether to create.
                    continue;
                }
            }

            // Slow path — Phase 1: under the creation lock, determine ephemeral / evict,
            // then release the lock before calling factory.  Holding creationLock across
            // a potentially slow ORT-initialisation call would serialise Dispose(),
            // EvictModelAsync(), and GetLeaseAsync() for all other keys behind one load.
            bool ephemeral;
            PoolEntry? lruEvicted1 = null;
            await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check disposal after waiting on the creation lock so we do not create a
                // new entry after the pool has been disposed concurrently.
                ObjectDisposedException.ThrowIf(disposed, this);

                // Double-check after acquiring the lock (another thread may have created the entry).
                // `continue` releases creationLock via the finally block and restarts the loop so
                // we acquire the gate via the fast path WITHOUT holding creationLock.
                if (entries.ContainsKey(key))
                {
                    continue;
                }

                // Enforce the pool limit by evicting the LRU idle entry only when at capacity.
                // Cold misses while pooledCount < maxSessions must not evict a warm idle session.
                // TryEvictLruIdle removes and marks the entry under the lock but returns it
                // for disposal outside the lock so expensive ORT teardown does not hold creationLock.
                lruEvicted1 = pooledCount >= maxSessions ? TryEvictLruIdle() : null;
                ephemeral = pooledCount >= maxSessions && lruEvicted1 is null;
            }
            finally
            {
                creationLock.Release();
            }

            // Dispose the evicted session outside the lock so expensive ORT teardown
            // does not block unrelated callers waiting on creationLock.
            lruEvicted1?.Dispose();

            // Phase 2: create the session OUTSIDE the creation lock so that Dispose(),
            // EvictModelAsync(), and GetLeaseAsync() for other keys are not blocked.
            InferenceSession session = await factory(cancellationToken).ConfigureAwait(false);
            var freshEntry = new PoolEntry(session, ephemeral);

            if (freshEntry.Ephemeral)
            {
                // Ephemeral entries are never stored; return the lease directly.
                return BuildLease(freshEntry);
            }

            // Phase 3: re-acquire the creation lock to publish the new entry atomically.
            // A concurrent thread may have created an entry for this key while our factory ran.
            bool published = false;
            PoolEntry? competitor = null;
            PoolEntry? lruEvicted2 = null;
            try
            {
                await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);

                    if (entries.TryGetValue(key, out competitor))
                    {
                        published = false;
                    }
                    else
                    {
                        // Only evict to make room when the pool is already at capacity (same as phase 1).
                        lruEvicted2 = pooledCount >= maxSessions ? TryEvictLruIdle() : null;
                        if (pooledCount >= maxSessions && lruEvicted2 is null)
                        {
                            freshEntry.MarkEphemeral();
                        }
                        else
                        {
                            published = entries.TryAdd(key, freshEntry);
                            if (!published)
                            {
                                entries.TryGetValue(key, out competitor);
                            }
                            else
                            {
                                pooledCount++;
                            }
                        }
                    }
                }
                finally
                {
                    creationLock.Release();
                }
            }
            catch
            {
                // Cancellation, pool disposal, or an unexpected error: entry was not stored,
                // so dispose freshEntry here to prevent a session leak.
                if (!published)
                {
                    freshEntry.Dispose();
                }

                lruEvicted2?.Dispose();
                throw;
            }

            // Dispose the evicted session outside the lock so expensive ORT teardown
            // does not block unrelated callers waiting on creationLock.
            lruEvicted2?.Dispose();

            if (published)
            {
                // The gate starts locked (count=0), representing the active lease for
                // the creator. Release() is called when the lease is disposed.
                return BuildLease(freshEntry);
            }

            if (freshEntry.Ephemeral)
            {
                return BuildLease(freshEntry);
            }

            // Lost the race: another thread concurrently published an entry for this key.
            // Discard our duplicate session and lease the winner's entry instead.
            freshEntry.Dispose();
            if (competitor is not null)
            {
                try
                {
                    await competitor.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (disposed)
                    {
                        ReleaseLeaseEntry(competitor);
                        ObjectDisposedException.ThrowIf(disposed, this);
                    }

                    return BuildLease(competitor);
                }
                catch (ObjectDisposedException)
                {
                    // Competitor was evicted between Phase 3 and here; restart the whole loop.
                    continue;
                }
            }
            // Competitor entry was evicted between TryGetValue and Gate.WaitAsync; restart.
            continue;
        }
    }

    /// <summary>
    /// Evicts every idle pooled session. Sessions currently leased out remain until released.
    /// </summary>
    /// <returns>The number of idle entries that were evicted and disposed.</returns>
    public async Task<int> EvictAllIdleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        List<PoolEntry> toDispose = new();
        int count = 0;

        await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            foreach (SessionPoolKey key in entries.Keys.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!entries.TryGetValue(key, out PoolEntry? entry))
                {
                    continue;
                }

                if (!TryAcquireGate(entry))
                {
                    continue;
                }

                if (entries.TryRemove(new KeyValuePair<SessionPoolKey, PoolEntry>(key, entry)))
                {
                    pooledCount--;
                    entry.MarkEvicted();
                    toDispose.Add(entry);
                    count++;
                }
                else
                {
                    entry.Gate.Release();
                }
            }
        }
        finally
        {
            creationLock.Release();
        }

        foreach (PoolEntry entry in toDispose)
        {
            entry.Dispose();
        }

        return count;
    }

    /// <summary>
    /// Evicts all idle pool entries whose <see cref="SessionPoolKey.EngineFamily"/> matches
    /// <paramref name="engineFamily"/> and, when specified, whose
    /// <see cref="SessionPoolKey.ModelId"/> matches <paramref name="modelId"/>.
    /// In-use sessions (leased out) are not evicted.
    /// </summary>
    /// <returns>The number of entries that were evicted and disposed.</returns>
    public async Task<int> EvictModelAsync(
        string engineFamily,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        ObjectDisposedException.ThrowIf(disposed, this);

        List<PoolEntry> toDispose = new();
        int count = 0;

        await creationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            foreach (SessionPoolKey key in entries.Keys.ToArray())
            {
                bool familyMatch = string.Equals(key.EngineFamily, engineFamily, StringComparison.OrdinalIgnoreCase);
                bool modelMatch = modelId is null || string.Equals(key.ModelId, modelId, StringComparison.OrdinalIgnoreCase);

                if (familyMatch && modelMatch && entries.TryGetValue(key, out PoolEntry? entry))
                {
                    if (!TryAcquireGate(entry))
                    {
                        continue;
                    }

                    if (entries.TryRemove(new KeyValuePair<SessionPoolKey, PoolEntry>(key, entry)))
                    {
                        pooledCount--;
                        entry.MarkEvicted();
                        toDispose.Add(entry); // dispose outside the lock below
                        count++;
                    }
                    else
                    {
                        entry.Gate.Release(); // entry removed by a concurrent eviction; release gate
                    }
                }
            }
        }
        finally
        {
            creationLock.Release();
        }

        // Dispose sessions outside the lock — session disposal can be expensive and
        // should not block other callers waiting on creationLock.
        foreach (PoolEntry entry in toDispose)
        {
            entry.Dispose();
        }

        return count;
    }

    /// <summary>
    /// Evicts idle pool entries until the estimated total VRAM footprint of pooled sessions
    /// is at or below <paramref name="targetVramMb"/>. Sessions currently leased out are not
    /// evicted. Returns the number of sessions evicted.
    /// </summary>
    /// <remarks>
    /// Eviction order: least-recently-released idle sessions first (same LRU order as the
    /// count-based eviction in <see cref="GetLeaseAsync"/>).
    /// </remarks>
    public async Task<int> TrimToVramBudgetAsync(long targetVramMb, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (targetVramMb < 0) throw new ArgumentOutOfRangeException(nameof(targetVramMb));

        List<PoolEntry> toDispose = new();
        int count = 0;

        await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // Compute current total estimated VRAM of idle sessions
            long currentVram = entries.Sum(kvp => kvp.Value.IsIdle ? kvp.Key.EstimatedVramMb : 0);
            if (currentVram <= targetVramMb)
                return 0;

            // Sort idle entries by LRU (oldest first)
            var idleEntries = entries
                .Where(kvp => kvp.Value.IsIdle)
                .OrderBy(kvp => kvp.Value.LastReleasedTicks)
                .ToList();

            foreach (var (key, entry) in idleEntries)
            {
                if (currentVram <= targetVramMb)
                    break;

                cancellationToken.ThrowIfCancellationRequested();

                if (!TryAcquireGate(entry))
                    continue;

                if (entries.TryRemove(new KeyValuePair<SessionPoolKey, PoolEntry>(key, entry)))
                {
                    pooledCount--;
                    entry.MarkEvicted();
                    currentVram -= key.EstimatedVramMb;
                    toDispose.Add(entry);
                    count++;
                }
                else
                {
                    entry.Gate.Release();
                }
            }
        }
        finally
        {
            creationLock.Release();
        }

        foreach (PoolEntry entry in toDispose)
            entry.Dispose();

        return count;
    }

    /// <summary>
    /// Disposes all idle pooled sessions.  Sessions currently leased out are left for their
    /// lease holders to release naturally.
    /// </summary>
    public void Dispose()
    {
        // Single-winner guard: only the first caller performs teardown.
        // Using Interlocked ensures concurrent Dispose() calls are safe and idempotent.
        if (Interlocked.Exchange(ref disposeOnce, 1) != 0)
        {
            return;
        }

        // Signal all code paths that check `disposed` so they throw ObjectDisposedException
        // instead of creating new sessions.  Must be set before acquiring the lock so that
        // slow-path waiters that win the lock after we release it observe the flag.
        disposed = true;

        List<PoolEntry> toDispose = new();

        if (creationLock.Wait(TimeSpan.FromSeconds(10)))
        {
            try
            {
                foreach (SessionPoolKey key in entries.Keys.ToArray())
                {
                    if (entries.TryRemove(key, out PoolEntry? entry))
                    {
                        pooledCount--;
                        entry.MarkEvicted(); // defence-in-depth alongside `disposed` flag
                        // Atomically try to acquire the gate. If we win, schedule the entry for
                        // disposal outside the lock. If the entry is still leased, the BuildLease
                        // closure will observe disposed=true (and IsEvicted=true) on release and
                        // call entry.Dispose() idempotently.
                        if (TryAcquireGate(entry))
                        {
                            toDispose.Add(entry);
                        }
                    }
                }
            }
            finally
            {
                // Release the semaphore — do NOT dispose it.  Concurrent GetLeaseAsync slow-path
                // waiters may already be blocked on creationLock.WaitAsync(); disposing the
                // semaphore here would race with those waiters and surface as ObjectDisposedException
                // instead of the clean ObjectDisposedException.ThrowIf(disposed, this) path.
                // The semaphore is a small object and will be collected by the GC after the pool
                // itself is no longer referenced.
                pooledCount = 0;
                creationLock.Release();
            }
        }

        // Dispose sessions outside the lock — session disposal can be expensive and
        // should not block concurrent callers waiting on creationLock.
        foreach (PoolEntry entry in toDispose)
        {
            entry.Dispose();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private SessionLease BuildLease(PoolEntry entry)
    {
        return new SessionLease(entry.Session, () => ReleaseLeaseEntry(entry));
    }

    private void ReleaseLeaseEntry(PoolEntry entry)
    {
        if (entry.Ephemeral)
        {
            // Ephemeral entries are never stored in the pool; dispose immediately.
            entry.Dispose();
            return;
        }

        // Release the gate BEFORE checking eviction / disposal flags.  This closes the
        // handshake race: an eviction path that called MarkEvicted() while we held the gate
        // and then observed Gate.Wait(0)==false backed off, expecting us to clean up after
        // releasing.  After Release() the eviction path can atomically re-acquire the gate
        // and dispose, or — if it has already backed off — we detect the eviction below and
        // perform disposal ourselves.
        entry.Release();

        if (entry.IsEvicted || disposed)
        {
            // Re-check after releasing the gate: if an eviction path marked this entry
            // while the gate was held and left cleanup to us, attempt to re-acquire the
            // gate atomically.  If another caller (fast-path GetLeaseAsync or an eviction
            // path that beat us) already holds the gate, cleanup is theirs.
            try
            {
                if (entry.Gate.Wait(0))
                {
                    entry.Dispose();
                }
            }
            catch (ObjectDisposedException)
            {
                // Gate already disposed by a concurrent eviction / pool-dispose path.
            }
        }
    }

    /// <summary>
    /// Evicts the least-recently-released idle entry.
    /// Must be called while <see cref="creationLock"/> is held.
    /// </summary>
    /// <returns>
    /// The evicted <see cref="PoolEntry"/> (gate already acquired, removed from the pool, marked evicted)
    /// when an idle entry was found and successfully evicted; <see langword="null"/> when all entries are
    /// currently leased.  The caller is responsible for disposing the returned entry <em>outside</em>
    /// <see cref="creationLock"/> to avoid blocking unrelated pool operations during potentially expensive
    /// <see cref="InferenceSession"/> teardown.
    /// </returns>
    private PoolEntry? TryEvictLruIdle()
    {
        SessionPoolKey? candidateKey = null;
        PoolEntry? candidateEntry = null;
        long candidateLastReleasedTicks = long.MaxValue;

        foreach (KeyValuePair<SessionPoolKey, PoolEntry> pair in entries)
        {
            PoolEntry entry = pair.Value;
            if (!entry.IsIdle)
            {
                continue;
            }

            long lastReleasedTicks = entry.LastReleasedTicks;
            if (candidateEntry is null || lastReleasedTicks < candidateLastReleasedTicks)
            {
                candidateKey = pair.Key;
                candidateEntry = entry;
                candidateLastReleasedTicks = lastReleasedTicks;
            }
        }

        if (candidateEntry is null)
        {
            return null;
        }

        if (!entries.TryGetValue(candidateKey!, out PoolEntry? evicted) || !TryAcquireGate(evicted))
        {
            return null;
        }

        bool removed = false;
        try
        {
            removed = entries.TryRemove(new KeyValuePair<SessionPoolKey, PoolEntry>(candidateKey!, evicted));
            if (removed)
            {
                pooledCount--;
                evicted.MarkEvicted();
                return evicted; // Caller must dispose outside creationLock.
            }
        }
        finally
        {
            if (!removed)
            {
                evicted.ReleaseGateWithoutTouchingLru();
            }
        }

        return null;
    }

    private static bool TryAcquireGate(PoolEntry entry)
    {
        try
        {
            return entry.Gate.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
