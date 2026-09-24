using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.Benchmarking;
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
/// The pool holds at most <c>maxSessions</c> live sessions across all keys (default: 12).
/// This is a <em>count-based</em> cap. Optional memory admission (off by default) adds a
/// VRAM budget: before construct, reserve <see cref="SessionPoolKey.EstimatedVramMb"/>,
/// evict idle sessions to fit, and wait — never allocate an unbudgeted ephemeral session.
/// When admission is off and the count limit is reached with every entry leased, the new
/// session is created outside the pool (ephemeral) and disposed when its lease is released.</para>
///
/// <para><strong>Single-flight creation:</strong>
/// Concurrent misses for the same key share one factory invocation. Cancelling one waiter
/// does not cancel another caller’s valid acquisition.</para>
///
/// <para><strong>Model invalidation:</strong>
/// Session keys are keyed by model file <em>path hash</em>, not file content.  If a model file
/// changes on disk at the same path (e.g. hot-swap during development), evict the stale entry
/// explicitly via <see cref="EvictModelAsync"/> or restart the process.  Content-hash
/// invalidation on every pool lookup is out of scope for this PR.</para>
///
/// <para><strong>Memory pressure guidance:</strong>
/// Each ONNX session can consume hundreds of MB of GPU/CPU memory depending on model size.
/// Prefer enabling memory admission with a realistic budget over relying on the count cap.
/// Call <see cref="EvictModelAsync"/> to free memory for a model that is no longer needed,
/// or <see cref="Dispose"/> to release the entire pool.</para>
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

    // Sized for a full dub working set (VAD + separation + diarization + ASR encoder/decoder
    // + translation encoder/decoder + multi-graph TTS) so LRU does not thrash mid-pipeline.
    private const int DefaultMaxSessions = 12;

    /// <summary>Default VRAM admission budget when <c>enableMemoryAdmission</c> is on.</summary>
    public const long DefaultMemoryBudgetMb = 4096;

    /// <summary>
    /// Idle sessions released within this window are treated as part of the active pipeline
    /// working set and are only evicted when no older idle entry exists.
    /// </summary>
    private const long RecentReleaseWindowMs = 120_000;

    private sealed class PoolEntry(InferenceSession session, bool ephemeral) : IDisposable
    {
        private long lastReleasedTicks = Environment.TickCount64;
        private volatile bool evicted;
        private int disposeState; // 0 = live, 1 = disposed; guarded by Interlocked for idempotency
        private int pinCount;

        public InferenceSession Session { get; } = session;

        /// <summary>Per-entry gate that serialises access (one user at a time).</summary>
        public SemaphoreSlim Gate { get; } = new(0, 1); // Starts unavailable because the creator immediately owns the first lease.

        /// <summary>
        /// Residency pins (audit §3A). Pinned entries are not idle-evicted. Independent of
        /// <see cref="Gate"/> — a session can be resident and still free for execution.
        /// </summary>
        public int PinCount => Volatile.Read(ref pinCount);

        public bool TryPin()
        {
            if (evicted || disposeState != 0)
            {
                return false;
            }

            Interlocked.Increment(ref pinCount);
            if (evicted || disposeState != 0)
            {
                Unpin();
                return false;
            }

            return true;
        }

        public void Unpin()
        {
            if (Interlocked.Decrement(ref pinCount) < 0)
            {
                Interlocked.Exchange(ref pinCount, 0);
            }
        }

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
    private readonly ConcurrentDictionary<SessionPoolKey, SemaphoreSlim> createGates = new();
    private readonly SemaphoreSlim creationLock = new(1, 1);
    private readonly SemaphoreSlim bundleAcquireLock = new(1, 1);
    private readonly bool enableMemoryAdmission;
    private readonly long memoryBudgetMb;
    /// <summary>Pending create reservations per device (audit §3A: budget is per physical device).</summary>
    private readonly ConcurrentDictionary<int, long> pendingCreateMbByDevice = new();
    private int admissionWaiters;
    private volatile bool disposed;
    private int pooledCount;
    private int disposeOnce; // 0 = not yet, 1 = disposed; Interlocked guard for single-winner teardown

    public InferenceSessionPool(
        int maxSessions = DefaultMaxSessions,
        bool enableMemoryAdmission = false,
        long memoryBudgetMb = DefaultMemoryBudgetMb)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSessions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryBudgetMb, 1);
        this.maxSessions = maxSessions;
        this.enableMemoryAdmission = enableMemoryAdmission;
        this.memoryBudgetMb = memoryBudgetMb;
    }

    /// <summary>
    /// Device ordinal used for admission accounting. Null means the default device (0).
    /// DirectML and TensorRT on the same GPU share this budget — provider is not part of the key.
    /// </summary>
    private static int DeviceOf(SessionPoolKey key) => key.DeviceId ?? 0;

    /// <summary>
    /// Returns an appropriate default max-session count for the current hardware.
    /// On Windows with a discrete NVIDIA GPU and >= 8 GB VRAM, returns 24 (covers a full
    /// multi-graph pipeline without mid-run LRU thrash). Otherwise returns 12.
    /// </summary>
    public static int RecommendedMaxSessions(IReadOnlyList<DeviceEntry>? devices)
    {
        if (devices is null)
            return DefaultMaxSessions;

        DeviceEntry? nvidiaGpu = devices.FirstOrDefault(d =>
            d.Kind == DeviceKind.DiscreteGpu &&
            string.Equals(d.VendorName, "NVIDIA", StringComparison.OrdinalIgnoreCase));

        if (nvidiaGpu is not null && nvidiaGpu.DedicatedVramMb >= 8192)
            return 24;

        return DefaultMaxSessions;
    }

    /// <summary>
    /// Pre-warms a session for <paramref name="key"/> without returning a lease.
    /// Useful at startup to amortise first-call latency.
    /// If a session for this key already exists the call is a no-op.
    /// The session is resident but <em>unpinned</em> and may be idle-evicted later.
    /// Use <see cref="GetResidencyAsync"/> when it must stay warm.
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
    /// Creates or reuses <paramref name="key"/> and returns a <see cref="SessionResidency"/> pin.
    /// The pin keeps the session out of idle eviction but does <em>not</em> hold the execution
    /// gate — other callers can still <see cref="GetLeaseAsync"/> and Run() (audit §3A:
    /// residency ≠ exclusivity). Dispose the residency to allow eviction again.
    /// </summary>
    public async Task<SessionResidency> GetResidencyAsync(
        SessionPoolKey key,
        Func<CancellationToken, Task<InferenceSession>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        while (true)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // Ensure the session exists (single-flight create). Release the lease immediately.
            using (await GetLeaseAsync(key, factory, cancellationToken).ConfigureAwait(false))
            {
            }

            if (entries.TryGetValue(key, out PoolEntry? entry) && entry.TryPin())
            {
                PoolEntry pinned = entry;
                return new SessionResidency(pinned.Unpin);
            }

            // Evicted between release and pin — retry create.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Pins an already-pooled session if present. Used after a create/warm when the caller
    /// re-acquires with the same <see cref="SessionPoolKey"/> for each execution.
    /// </summary>
    public bool TryPinExisting(SessionPoolKey key, out SessionResidency? residency)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (entries.TryGetValue(key, out PoolEntry? entry) && entry.TryPin())
        {
            PoolEntry pinned = entry;
            residency = new SessionResidency(pinned.Unpin);
            return true;
        }

        residency = null;
        return false;
    }

    /// <summary>
    /// Returns an exclusive <see cref="SessionLease"/> for <paramref name="key"/>.
    /// If no session exists for the key, one is created using <paramref name="factory"/>
    /// (single-flight: concurrent misses share one factory call).
    /// When memory admission is enabled the create waits for budget after evicting idle
    /// sessions and never allocates an unbudgeted ephemeral session. When admission is off
    /// and the pool is full with no idle eviction target, the session is created outside
    /// the pool (ephemeral) and disposed when the lease is released.
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
                    using (BenchmarkPhaseCapture.Start("pool-wait"))
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

            // Single-flight create: one factory invocation per key. Cancelling this waiter
            // does not cancel the shared create for other callers.
            SemaphoreSlim createGate = createGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
            await createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (entries.ContainsKey(key))
                {
                    continue;
                }

                long needMb = ResolveReservationMb(key);
                int device = DeviceOf(key);
                bool ephemeral = false;
                PoolEntry? lruEvicted1 = null;
                bool reserved = false;
                using (BenchmarkPhaseCapture.Start("pool-creation-lock-wait"))
                    await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (entries.ContainsKey(key))
                    {
                        if (reserved)
                        {
                            ReleaseReservation(device, needMb);
                        }
                        continue;
                    }
                    }

                    if (enableMemoryAdmission)
                    {
                        // Evict idle sessions on this device until the reservation fits.
                        // Other devices have their own budgets (shared across EPs on one GPU).
                        while (CurrentReservedMb(device) + needMb > memoryBudgetMb)
                        {
                            PoolEntry? evictedForBudget = TryEvictLruIdle(onlyDevice: device);
                            if (evictedForBudget is null)
                            {
                                break;
                            }

                            evictedForBudget.Dispose();
                        }

                        if (CurrentReservedMb(device) + needMb <= memoryBudgetMb)
                        {
                            AddPendingReservation(device, needMb);
                            reserved = true;
                        }
                        // else: do not hold a reservation while waiting — that deadlocks
                        // when every waiter reserves and nobody can release.
                    }
                    else
                    {
                        lruEvicted1 = pooledCount >= maxSessions ? TryEvictLruIdle() : null;
                        ephemeral = pooledCount >= maxSessions && lruEvicted1 is null;
                    }
                }
                finally
                {
                    creationLock.Release();
                }

                if (enableMemoryAdmission && !reserved)
                {
                    await WaitForAdmissionBudgetAsync(needMb, device, cancellationToken).ConfigureAwait(false);
                    // Budget reserved by WaitForAdmissionBudgetAsync on success.
                    reserved = true;
                }

                lruEvicted1?.Dispose();

                InferenceSession session;
                try
                {
                    using (BenchmarkPhaseCapture.Start("session-create"))
                        session = await factory(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (reserved)
                    {
                        ReleaseReservation(device, needMb);
                    }

                    throw;
                }

                var freshEntry = new PoolEntry(session, ephemeral);

                if (freshEntry.Ephemeral)
                {
                    if (reserved)
                    {
                        ReleaseReservation(device, needMb);
                    }

                    return BuildLease(freshEntry);
                }

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
                            if (!enableMemoryAdmission)
                            {
                                lruEvicted2 = pooledCount >= maxSessions ? TryEvictLruIdle() : null;
                                if (pooledCount >= maxSessions && lruEvicted2 is null)
                                {
                                    freshEntry.MarkEphemeral();
                                }
                            }

                            if (!freshEntry.Ephemeral)
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
                    if (!published)
                    {
                        freshEntry.Dispose();
                    }

                    if (reserved)
                    {
                        ReleaseReservation(device, needMb);
                    }

                    lruEvicted2?.Dispose();
                    throw;
                }

                lruEvicted2?.Dispose();

                if (published)
                {
                    // Estimate now lives on the pooled entry (CurrentReservedMb sums entries);
                    // drop the pending reservation so it is not double-counted.
                    if (reserved)
                    {
                        ReleaseReservation(device, needMb);
                    }

                    return BuildLease(freshEntry);
                }

                if (freshEntry.Ephemeral)
                {
                    if (reserved)
                    {
                        ReleaseReservation(device, needMb);
                    }

                    return BuildLease(freshEntry);
                }

                // Lost the race: another thread concurrently published an entry for this key.
                // Discard our duplicate session and lease the winner's entry instead.
                freshEntry.Dispose();
                if (reserved)
                {
                    ReleaseReservation(device, needMb);
                }

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
                        continue;
                    }
                }

                continue;
            }
            finally
            {
                createGate.Release();
            }
        }
    }

    private long ResolveReservationMb(SessionPoolKey key) =>
        key.EstimatedVramMb > 0 ? key.EstimatedVramMb : SessionPoolKey.DefaultEstimatedVramMb;

    /// <summary>
    /// Acquires every graph in <paramref name="requests"/> as one all-or-nothing bundle
    /// (audit §3A: never hold an encoder while waiting indefinitely for a decoder).
    /// Sessions are created first (single-flight), then exclusive gates are taken in
    /// <see cref="SessionPoolKey.StableComparer"/> order without blocking on a later
    /// key while holding an earlier one — if any gate is busy the attempt rolls back
    /// and retries. Duplicate keys are rejected.
    /// </summary>
    public async Task<SessionLeaseBundle> GetLeaseBundleAsync(
        IReadOnlyList<SessionLeaseRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("Bundle requires at least one session.", nameof(requests));
        }

        var ordered = requests
            .OrderBy(r => r.Key, SessionPoolKey.StableComparer)
            .ToArray();
        for (int i = 1; i < ordered.Length; i++)
        {
            if (Equals(ordered[i - 1].Key, ordered[i].Key))
            {
                throw new ArgumentException(
                    "Bundle contains duplicate session keys; multi-graph bundles require distinct graphs.",
                    nameof(requests));
            }
        }

        // Phase 1: make sure every session exists (warm). Individual GetLeaseAsync is
        // single-flight and safe here — we release immediately so no gate is held.
        foreach (SessionLeaseRequest request in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SessionLease warm = await GetLeaseAsync(request.Key, request.Factory, cancellationToken)
                .ConfigureAwait(false);
        }

        // Phase 2: all-or-nothing exclusive acquire in stable order. TryWait(0) per key
        // so we never block on key N while holding keys 1..N-1. One bundle at a time
        // (bundleAcquireLock) so opposing caller orders cannot livelock each other.
        await bundleAcquireLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();

                var held = new List<(SessionPoolKey Key, PoolEntry Entry)>(ordered.Length);
                bool allAcquired = true;
                foreach (SessionLeaseRequest request in ordered)
                {
                    if (!entries.TryGetValue(request.Key, out PoolEntry? entry) || !TryAcquireGate(entry))
                    {
                        allAcquired = false;
                        break;
                    }

                    if (entry.IsEvicted)
                    {
                        entry.ReleaseGateWithoutTouchingLru();
                        allAcquired = false;
                        break;
                    }

                    held.Add((request.Key, entry));
                }

                if (allAcquired && held.Count == ordered.Length)
                {
                    var leases = new SessionLease[ordered.Length];
                    for (int i = 0; i < ordered.Length; i++)
                    {
                        SessionPoolKey key = ordered[i].Key;
                        int requestIndex = -1;
                        for (int r = 0; r < requests.Count; r++)
                        {
                            if (Equals(requests[r].Key, key))
                            {
                                requestIndex = r;
                                break;
                            }
                        }

                        PoolEntry entry = held.First(h => Equals(h.Key, key)).Entry;
                        leases[requestIndex] = BuildLease(entry);
                    }

                    return new SessionLeaseBundle(leases);
                }

                foreach ((SessionPoolKey _, PoolEntry entry) in held)
                {
                    entry.ReleaseGateWithoutTouchingLru();
                }

                lock (createGates)
                {
                    Monitor.Wait(createGates, 10);
                }
            }
        }
        finally
        {
            bundleAcquireLock.Release();
        }
    }

    private long CurrentReservedMb(int device)
    {
        long pooled = 0;
        foreach (KeyValuePair<SessionPoolKey, PoolEntry> pair in entries)
        {
            if (DeviceOf(pair.Key) == device)
            {
                pooled += ResolveReservationMb(pair.Key);
            }
        }

        pendingCreateMbByDevice.TryGetValue(device, out long pending);
        return pooled + pending;
    }

    private void AddPendingReservation(int device, long mb) =>
        pendingCreateMbByDevice.AddOrUpdate(device, mb, (_, existing) => existing + mb);

    private void ReleaseReservation(int device, long mb)
    {
        pendingCreateMbByDevice.AddOrUpdate(device, 0, (_, existing) => Math.Max(0, existing - mb));
        lock (createGates)
        {
            Monitor.PulseAll(createGates);
        }
    }

    /// <summary>
    /// Waits until <paramref name="needMb"/> fits in <paramref name="device"/>'s budget
    /// (evicting idle sessions on that device as needed), then takes the reservation.
    /// Never holds a reservation while waiting. Budget is shared across EPs on one device.
    /// </summary>
    private async Task WaitForAdmissionBudgetAsync(long needMb, int device, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref admissionWaiters);
        try
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();

                bool acquired = false;
                List<PoolEntry>? toDispose = null;
                await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    while (CurrentReservedMb(device) + needMb > memoryBudgetMb)
                    {
                        PoolEntry? evicted = TryEvictLruIdle(onlyDevice: device);
                        if (evicted is null)
                        {
                            break;
                        }

                        toDispose ??= new List<PoolEntry>();
                        toDispose.Add(evicted);
                    }

                    if (CurrentReservedMb(device) + needMb <= memoryBudgetMb)
                    {
                        AddPendingReservation(device, needMb);
                        acquired = true;
                    }
                }
                finally
                {
                    creationLock.Release();
                }

                if (toDispose is not null)
                {
                    foreach (PoolEntry entry in toDispose)
                    {
                        entry.Dispose();
                    }
                }

                if (acquired)
                {
                    return;
                }

                lock (createGates)
                {
                    if (CurrentReservedMb(device) + needMb <= memoryBudgetMb)
                    {
                        // Fits now; loop to take the reservation under creationLock.
                    }
                    else
                    {
                        Monitor.Wait(createGates, 50);
                    }
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref admissionWaiters);
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
        ArgumentOutOfRangeException.ThrowIfNegative(targetVramMb);

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
            // Admission mode never creates ephemeral sessions.
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
    /// <summary>
    /// Evicts the least-recently-released idle entry, optionally restricted to one device
    /// (so a budget shortfall on GPU 0 is not “fixed” by dropping GPU 1 sessions).
    /// Must be called while <see cref="creationLock"/> is held.
    /// </summary>
    private PoolEntry? TryEvictLruIdle(int? onlyDevice = null)
    {
        // Prefer evicting entries that have been idle for a while. Sessions released within
        // the recent window are treated as the active pipeline working set (e.g. the next
        // stage's graphs) and are only chosen when nothing older is idle.
        long now = Environment.TickCount64;
        long recentCutoff = now - RecentReleaseWindowMs;

        SessionPoolKey? candidateKey = null;
        PoolEntry? candidateEntry = null;
        long candidateLastReleasedTicks = long.MaxValue;

        foreach (KeyValuePair<SessionPoolKey, PoolEntry> pair in entries)
        {
            if (onlyDevice is not null && DeviceOf(pair.Key) != onlyDevice.Value)
            {
                continue;
            }

            PoolEntry entry = pair.Value;
            if (!entry.IsIdle)
            {
                continue;
            }

            long lastReleasedTicks = entry.LastReleasedTicks;
            if (lastReleasedTicks >= recentCutoff)
            {
                continue;
            }

            if (entry.PinCount > 0)
            {
                continue;
            }

            if (candidateEntry is null || lastReleasedTicks < candidateLastReleasedTicks)
            {
                candidateKey = pair.Key;
                candidateEntry = entry;
                candidateLastReleasedTicks = lastReleasedTicks;
            }
        }

        if (candidateEntry is null)
        {
            foreach (KeyValuePair<SessionPoolKey, PoolEntry> pair in entries)
            {
                if (onlyDevice is not null && DeviceOf(pair.Key) != onlyDevice.Value)
                {
                    continue;
                }

                PoolEntry entry = pair.Value;
                if (!entry.IsIdle || entry.PinCount > 0)
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
