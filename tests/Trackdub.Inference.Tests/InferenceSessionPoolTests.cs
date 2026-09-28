using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.TestDoubles;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Unit tests for <see cref="InferenceSessionPool"/>.
/// Tests that require creating real <see cref="InferenceSession"/> objects are guarded by
/// <see cref="RequiresBundledModelFactAttribute"/> or use a fake session factory.
/// </summary>
public sealed class InferenceSessionPoolTests
{
    private static readonly SessionPoolKey TestKey = new(
        "test-engine", "test-model", null, ExecutionProviderKind.Cpu, "abc123", 0, "default");

    // ── Shared pool ───────────────────────────────────────────────────────────

    [Fact]
    public void Shared_IsSingleton()
    {
        Assert.Same(InferenceSessionPool.Shared, InferenceSessionPool.Shared);
    }

    // ── Constructor guard ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ZeroMaxSessions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InferenceSessionPool(0));
    }

    [Fact]
    public void Constructor_NegativeMaxSessions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InferenceSessionPool(-1));
    }

    [Fact]
    public void Constructor_OneMaxSession_DoesNotThrow()
    {
        using var pool = new InferenceSessionPool(1);
        Assert.NotNull(pool);
    }

    // ── Dispose guard ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLeaseAsync_AfterDispose_Throws()
    {
        var pool = new InferenceSessionPool(4);
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.GetLeaseAsync(TestKey, _ => Task.FromException<InferenceSession>(new Exception("should not reach")), CancellationToken.None));
    }

    [Fact]
    public async Task WarmAsync_AfterDispose_Throws()
    {
        var pool = new InferenceSessionPool(4);
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.WarmAsync(TestKey, _ => Task.FromException<InferenceSession>(new Exception("should not reach")), CancellationToken.None));
    }

    [Fact]
    public async Task EvictModelAsync_AfterDispose_Throws()
    {
        var pool = new InferenceSessionPool(4);
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.EvictModelAsync("test-engine"));
    }

    // ── Dispose idempotency ───────────────────────────────────────────────────

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var pool = new InferenceSessionPool(4);
        pool.Dispose();
        pool.Dispose(); // must not throw
    }

    // ── SessionLease ──────────────────────────────────────────────────────────

    [Fact]
    public void SessionLease_Dispose_CalledTwice_DoesNotThrow()
    {
        // Ensure the double-dispose guard in SessionLease works.
        int releaseCount = 0;

        var session = CreateMinimalSession();
        try
        {
            var lease = new SessionLease(session, () => releaseCount++);
            lease.Dispose();
            lease.Dispose(); // must not throw or call release twice

            Assert.Equal(1, releaseCount);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void SessionLease_Dispose_InvokesRelease()
    {
        bool released = false;
        var session = CreateMinimalSession();
        try
        {
            using (new SessionLease(session, () => released = true))
            {
                Assert.False(released);
            }

            Assert.True(released);
        }
        finally
        {
            session.Dispose();
        }
    }

    // ── EvictModelAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task EvictModelAsync_NoMatchingEntries_ReturnsZero()
    {
        using var pool = new InferenceSessionPool(4);

        int evicted = await pool.EvictModelAsync("nonexistent-engine");

        Assert.Equal(0, evicted);
    }

    [Fact]
    public async Task EvictModelAsync_DoesNotEvictLeasedEntry()
    {
        using var pool = new InferenceSessionPool(2);
        var key = new SessionPoolKey("eng", "model", null, ExecutionProviderKind.Cpu, "hash1", 0, "default");

        using (await pool.GetLeaseAsync(key, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None))
        {
            int evicted = await pool.EvictModelAsync("eng", "model");

            Assert.Equal(0, evicted);
        }

        int factoryCalls = 0;
        using (await pool.GetLeaseAsync(
            key,
            _ => { factoryCalls++; return Task.FromResult(CreateMinimalSession()); },
            CancellationToken.None))
        {
        }

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task GetLeaseAsync_WaitingFastPathThrows_WhenPoolDisposed()
    {
        var pool = new InferenceSessionPool(1);
        var key = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash1", 0, "default");

        using SessionLease activeLease = await pool.GetLeaseAsync(
            key,
            _ => Task.FromResult(CreateMinimalSession()),
            CancellationToken.None);

        Task<SessionLease> waitingLease = pool.GetLeaseAsync(
            key,
            _ => Task.FromResult(CreateMinimalSession()),
            CancellationToken.None);

        pool.Dispose();
        activeLease.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waitingLease);
    }

    // ── Single-flight creation ───────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentColdMisses_ConstructOneSession()
    {
        using var pool = new InferenceSessionPool(4);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;
        Func<CancellationToken, Task<InferenceSession>> factory = async _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            await releaseFactory.Task;
            return CreateMinimalSession();
        };

        Task<SessionLease> creator = pool.GetLeaseAsync(TestKey, factory, CancellationToken.None);
        Task<SessionLease> waiter1 = pool.GetLeaseAsync(TestKey, factory, CancellationToken.None);
        Task<SessionLease> waiter2 = pool.GetLeaseAsync(TestKey, factory, CancellationToken.None);
        releaseFactory.SetResult();

        // Leases on one key are exclusive, so take them in whatever order they are granted.
        var remaining = new List<Task<SessionLease>> { creator, waiter1, waiter2 };
        InferenceSession? session = null;
        while (remaining.Count > 0)
        {
            Task<SessionLease> granted = await Task.WhenAny(remaining);
            remaining.Remove(granted);
            using SessionLease lease = await granted;
            session ??= lease.Session;
            Assert.Same(session, lease.Session);
        }

        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task CancellingTheCreator_LetsWaiterCreateInstead()
    {
        using var pool = new InferenceSessionPool(4);
        var creatorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;
        using var creatorCts = new CancellationTokenSource();

        Task<SessionLease> creator = pool.GetLeaseAsync(
            TestKey,
            async ct =>
            {
                Interlocked.Increment(ref factoryCalls);
                creatorStarted.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return CreateMinimalSession();
            },
            creatorCts.Token);
        await creatorStarted.Task;

        Task<SessionLease> waiter = pool.GetLeaseAsync(
            TestKey,
            _ => { Interlocked.Increment(ref factoryCalls); return Task.FromResult(CreateMinimalSession()); },
            CancellationToken.None);
        creatorCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator);
        using SessionLease lease = await waiter;
        Assert.NotNull(lease.Session);
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public async Task CancellingAWaiter_DoesNotCancelTheCreator()
    {
        using var pool = new InferenceSessionPool(4);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waiterCts = new CancellationTokenSource();

        Task<SessionLease> creator = pool.GetLeaseAsync(
            TestKey,
            async _ => { await releaseFactory.Task; return CreateMinimalSession(); },
            CancellationToken.None);
        Task<SessionLease> waiter = pool.GetLeaseAsync(
            TestKey,
            _ => Task.FromException<InferenceSession>(new InvalidOperationException("waiter must not build")),
            waiterCts.Token);

        waiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        releaseFactory.SetResult();
        using SessionLease lease = await creator;
        Assert.NotNull(lease.Session);
    }

    [Fact]
    public async Task CreatorFailure_PropagatesToWaiters()
    {
        using var pool = new InferenceSessionPool(4);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;
        Func<CancellationToken, Task<InferenceSession>> factory = async _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            await releaseFactory.Task;
            throw new InvalidOperationException("model load failed");
        };

        Task<SessionLease> creator = pool.GetLeaseAsync(TestKey, factory, CancellationToken.None);
        Task<SessionLease> waiter = pool.GetLeaseAsync(TestKey, factory, CancellationToken.None);
        releaseFactory.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => creator);
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiter);
        Assert.Equal(1, factoryCalls);
    }

    // ── LRU eviction & ephemeral fallback ────────────────────────────────────

    [Fact]
    public async Task LruEviction_EjectsOldestIdleEntry_WhenCapacityExceeded()
    {
        // Count-mode overflow behaviour only exists with admission explicitly disabled.
        using var pool = new InferenceSessionPool(maxSessions: 2, enableMemoryAdmission: false);

        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash1", 0, "default");
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash2", 0, "default");
        var key3 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash3", 0, "default");

        // Warm key1, then key2; key1 will have an older LastReleasedTicks and be the LRU candidate.
        // Spin until TickCount64 advances to guarantee key2's release stamp is strictly greater
        // than key1's, regardless of the platform's timer resolution.
        using (await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }
        // Spin until TickCount64 advances to guarantee key2's release stamp is strictly greater
        // than key1's, regardless of the platform's timer resolution.
        // Bounded to 5 s to prevent indefinite hangs in constrained/paused environments.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long t0 = Environment.TickCount64;
        while (Environment.TickCount64 == t0)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("Timer did not advance within 5 seconds.");
            }
            await Task.Delay(1);
        }
        using (await pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }

        // Pool at capacity (2: key1, key2). Request key3 → should evict key1 (LRU).
        using (await pool.GetLeaseAsync(key3, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }

        // key1 was evicted; requesting it must invoke the factory again.
        int key1FactoryCalls = 0;
        using (await pool.GetLeaseAsync(
            key1,
            _ => { key1FactoryCalls++; return Task.FromResult(CreateMinimalSession()); },
            CancellationToken.None)) { }

        Assert.Equal(1, key1FactoryCalls);
    }

    [Fact]
    public async Task ColdMiss_WithSpareCapacity_DoesNotEvictExistingIdleEntry()
    {
        using var pool = new InferenceSessionPool(maxSessions: 8);

        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash1", 0, "default");
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash2", 0, "default");

        using (await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }
        using (await pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }

        int key1FactoryCalls = 0;
        using (await pool.GetLeaseAsync(
            key1,
            _ => { key1FactoryCalls++; return Task.FromResult(CreateMinimalSession()); },
            CancellationToken.None))
        {
        }

        Assert.Equal(0, key1FactoryCalls);
    }

    [Fact]
    public async Task ReacquireAtCapacity_DoesNotCallFactory_EntryReused()
    {
        // Pool at capacity (maxSessions == 2, two idle entries). Re-acquiring an
        // existing key must be a fast-path hit: factory is never called and no
        // eviction occurs, even though pooledCount == maxSessions.
        using var pool = new InferenceSessionPool(maxSessions: 2);

        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash1", 0, "default");
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash2", 0, "default");

        using (await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }
        using (await pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }

        int key1FactoryCalls = 0;
        using (await pool.GetLeaseAsync(
            key1,
            _ => { key1FactoryCalls++; return Task.FromResult(CreateMinimalSession()); },
            CancellationToken.None))
        {
        }

        Assert.Equal(0, key1FactoryCalls);
    }

    [Fact]
    public async Task LruEviction_DoesNotEvictLeasedEntry_FallsBackToEphemeral()
    {
        // Count-mode ephemeral overflow exists only with admission explicitly disabled.
        using var pool = new InferenceSessionPool(maxSessions: 1, enableMemoryAdmission: false);

        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash1", 0, "default");
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash2", 0, "default");

        // Acquire key1 and hold the lease (not released) so it cannot be evicted.
        using var lease1 = await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

        // Pool full (1) and the only entry is leased → key2 must be created as ephemeral.
        int key2Calls = 0;
        using (await pool.GetLeaseAsync(
            key2,
            _ => { key2Calls++; return Task.FromResult(CreateMinimalSession()); },
            CancellationToken.None)) { }

        Assert.Equal(1, key2Calls); // created once (ephemeral — not cached)

        // Because key2 was ephemeral, a second request must call the factory again.
        using (await pool.GetLeaseAsync(
            key2,
            _ => { key2Calls++; return Task.FromResult(CreateMinimalSession()); },
            CancellationToken.None)) { }

        Assert.Equal(2, key2Calls); // factory called again; key2 was never stored in the pool
    }

    [Fact]
    public async Task LruEviction_SkipsLeasedEntry_AndEvictsIdleCandidate()
    {
        // Count-mode eviction, so admission is explicitly disabled.
        using var pool = new InferenceSessionPool(maxSessions: 2, enableMemoryAdmission: false);

        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash1", 0, "default");
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash2", 0, "default");
        var key3 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash3", 0, "default");

        using SessionLease key1Lease = await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);
        using (await pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }

        using (await pool.GetLeaseAsync(key3, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }

        int key2FactoryCalls = 0;
        using (await pool.GetLeaseAsync(
            key2,
            _ => { key2FactoryCalls++; return Task.FromResult(CreateMinimalSession()); },
            CancellationToken.None))
        {
        }

        Assert.Equal(1, key2FactoryCalls);
    }

    [Fact]
    public async Task EphemeralLease_SessionIsValid_AndReleasesWithoutThrowing()
    {
        using var pool = new InferenceSessionPool(maxSessions: 1, enableMemoryAdmission: false);

        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash1", 0, "default");
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "hash2", 0, "default");

        // key1 held so pool is full and no eviction is possible.
        using var lease1 = await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

        // key2 must be created as ephemeral.
        SessionLease ephemeralLease = await pool.GetLeaseAsync(
            key2,
            _ => Task.FromResult(CreateMinimalSession()),
            CancellationToken.None);

        Assert.NotNull(ephemeralLease.Session);

        // Disposing an ephemeral lease must not throw.
        ephemeralLease.Dispose();
    }

    // ── Single-flight creation ────────────────────────────────────────────────

    [Fact]
    public async Task GetLeaseAsync_ConcurrentMisses_SameKey_InvokesFactoryOnce()
    {
        using var pool = new InferenceSessionPool(maxSessions: 4);
        int factoryCalls = 0;

        Task<InferenceSession> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.Delay(50).ContinueWith(
                _ => CreateMinimalSession(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // Same key is exclusive (one lease at a time), so collect leases sequentially:
        // the point is that the second acquire must not rebuild the session.
        Task<SessionLease> first = pool.GetLeaseAsync(TestKey, Factory, CancellationToken.None);
        Task<SessionLease> second = pool.GetLeaseAsync(TestKey, Factory, CancellationToken.None);

        SessionLease lease1 = await first;
        Assert.Equal(1, factoryCalls);
        lease1.Dispose();

        SessionLease lease2 = await second;
        lease2.Dispose();
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task GetLeaseAsync_CancelledWaiter_DoesNotCancelSharedCreate()
    {
        using var pool = new InferenceSessionPool(maxSessions: 4);
        int factoryCalls = 0;
        using var creatorCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var waiterCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Task<SessionLease> creator = pool.GetLeaseAsync(
            TestKey,
            async _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                await Task.Delay(300);
                return CreateMinimalSession();
            },
            creatorCts.Token);

        await Task.Delay(20);
        Task<SessionLease> waiter = pool.GetLeaseAsync(
            TestKey,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult(CreateMinimalSession());
            },
            waiterCts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        SessionLease lease = await creator;
        lease.Dispose();

        Assert.Equal(1, factoryCalls);
    }

    // ── Memory admission (default-on hard admission) ─────────────────────────────────────────────

    [Fact]
    public async Task MemoryAdmission_DisabledExplicitly_StillAllowsEphemeral()
    {
        // Explicit opt-out preserves the historical count-mode ephemeral overflow.
        using var pool = new InferenceSessionPool(maxSessions: 1, enableMemoryAdmission: false);
        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "h1", 0, "default") { EstimatedVramMb = 64 };
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "h2", 0, "default") { EstimatedVramMb = 64 };

        using SessionLease lease1 = await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);
        using SessionLease lease2 = await pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);
        Assert.NotNull(lease2.Session);
    }

    [Fact]
    public async Task MemoryAdmission_WaitsInsteadOfEphemeral_WhenBudgetExhausted()
    {
        using var pool = new InferenceSessionPool(
            maxSessions: 4,
            enableMemoryAdmission: true,
            memoryBudgetMb: 100,
            hostMemoryBudgetMb: 100);
        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "h1", 0, "default") { EstimatedVramMb = 80 };
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "h2", 0, "default") { EstimatedVramMb = 80 };

        SessionLease lease1 = await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        // key2 needs 80MB while key1 holds 80MB against a 100MB budget — must wait, not go ephemeral.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), cts.Token));

        lease1.Dispose();

        using SessionLease lease2 = await pool.GetLeaseAsync(
            key2,
            _ => Task.FromResult(CreateMinimalSession()),
            CancellationToken.None);
        Assert.NotNull(lease2.Session);
    }

    [Fact]
    public async Task MemoryAdmission_EvictsIdleToFit_InsteadOfWaiting()
    {
        using var pool = new InferenceSessionPool(
            maxSessions: 8,
            enableMemoryAdmission: true,
            memoryBudgetMb: 120,
            hostMemoryBudgetMb: 120);
        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "h1", 0, "default") { EstimatedVramMb = 80 };
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "h2", 0, "default") { EstimatedVramMb = 80 };

        using (await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None))
        {
        }

        // key1 is idle and must be evicted so key2 (80MB) fits in the 120MB budget.
        using SessionLease lease2 = await pool.GetLeaseAsync(
            key2,
            _ => Task.FromResult(CreateMinimalSession()),
            CancellationToken.None);
        Assert.NotNull(lease2.Session);
    }

    [Fact]
    public async Task MemoryAdmission_BudgetIsPerDevice_AndSharedAcrossEps()
    {
        // Same budget number applies per device. DML vs TRT is irrelevant — DeviceId is the bucket.
        using var pool = new InferenceSessionPool(
            maxSessions: 8,
            enableMemoryAdmission: true,
            memoryBudgetMb: 100);
        var gpu0Dml = new SessionPoolKey("eng", null, null, ExecutionProviderKind.DirectMl, "h0a", 0, "default") { EstimatedVramMb = 80 };
        var gpu0Trt = new SessionPoolKey("eng", null, null, ExecutionProviderKind.TensorRTRtx, "h0b", 0, "default") { EstimatedVramMb = 80 };
        var gpu1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.DirectMl, "h1", 1, "default") { EstimatedVramMb = 80 };

        using SessionLease holdGpu0 = await pool.GetLeaseAsync(
            gpu0Dml, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

        // GPU 1 has its own 100MB budget — must fit even though GPU 0 holds 80MB.
        using SessionLease takeGpu1 = await pool.GetLeaseAsync(
            gpu1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);
        Assert.NotNull(takeGpu1.Session);

        // GPU 0 TRT shares the GPU 0 budget with DML — must wait.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetLeaseAsync(gpu0Trt, _ => Task.FromResult(CreateMinimalSession()), cts.Token));
    }

    [Fact]
    public async Task GetLeaseAsync_PhaseCapture_RecordsTruthfulPoolCountersAndMaxima()
    {
        using var pool = new InferenceSessionPool(
            maxSessions: 4,
            enableMemoryAdmission: true,
            memoryBudgetMb: 100,
            hostMemoryBudgetMb: 100);
        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "pc1", 0, "default") { EstimatedVramMb = 80 };
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "pc2", 0, "default") { EstimatedVramMb = 80 };

        var capture = new BenchmarkPhaseCapture();
        using (BenchmarkPhaseCapture.Activate(capture))
        {
            // Cold acquire: miss + create, reservation observed while pending.
            SessionLease lease1 = await pool.GetLeaseAsync(
                key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

            // 80MB held of a 100MB device budget — key2 becomes an admission waiter.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), cts.Token));

            // Releasing key1 frees budget; key2 misses and creates (evicting idle key1).
            lease1.Dispose();
            using (await pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None))
            {
            }

            // Reacquire of the published key2 entry is a true hit: no factory call.
            using SessionLease reacquired = await pool.GetLeaseAsync(
                key2,
                _ => Task.FromException<InferenceSession>(new Exception("factory must not run on a hit")),
                CancellationToken.None);
        }

        IReadOnlyDictionary<string, long> counters = capture.SnapshotCounters();
        // The canceled admission waiter is still a miss — one miss or hit per acquire
        // request, while sessionCreate counts actual factory invocations.
        Assert.Equal(3, counters["poolMiss"]);
        Assert.Equal(2, counters["sessionCreate"]);
        Assert.Equal(1, counters["poolHit"]);

        IReadOnlyDictionary<string, long> maxima = capture.SnapshotMaxima();
        Assert.Equal(1, maxima["admissionWaiters"]);
        Assert.Equal(80, maxima["pendingReservationMb"]);
    }

    // ── Residency ≠ execution exclusivity (audit §3A) ─────────────────────────

    [Fact]
    public async Task GetResidencyAsync_PinsWithoutHoldingExecutionGate()
    {
        using var pool = new InferenceSessionPool(maxSessions: 4);
        SessionResidency residency = await pool.GetResidencyAsync(
            TestKey, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

        // Pin held, but a lease must still be acquirable — residency is not exclusivity.
        using SessionLease lease = await pool.GetLeaseAsync(
            TestKey, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);
        Assert.NotNull(lease.Session);
        residency.Dispose();
    }

    [Fact]
    public async Task GetResidencyAsync_PinnedSession_IsNotIdleEvicted()
    {
        using var pool = new InferenceSessionPool(
            maxSessions: 4,
            enableMemoryAdmission: true,
            memoryBudgetMb: 100,
            hostMemoryBudgetMb: 100);
        var key1 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "p1", 0, "default") { EstimatedVramMb = 80 };
        var key2 = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "p2", 0, "default") { EstimatedVramMb = 80 };

        SessionResidency pin = await pool.GetResidencyAsync(
            key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

        // key1 is idle (no lease) but pinned — must not be evicted to fit key2.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), cts.Token));

        // Unpin: key1 becomes eligible and is evicted so key2 fits.
        pin.Dispose();
        using SessionLease lease2 = await pool.GetLeaseAsync(
            key2, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);
        Assert.NotNull(lease2.Session);
    }

    [Fact]
    public void SessionPoolKey_EstimateVramMb_FromFileSize()
    {
        string path = Path.Join(Path.GetTempPath(), $"vram-est-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[3 * 1024 * 1024]); // 3 MB → 3*2+128 = 134
            long estimate = SessionPoolKey.EstimateVramMb(path);
            Assert.Equal(134, estimate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Multi-graph lease bundles (audit §3A) ────────────────────────────────

    private static SessionPoolKey KeyA() =>
        new("dual", null, null, ExecutionProviderKind.Cpu, "hashA", 0, "encoder") { EstimatedVramMb = 32 };

    private static SessionPoolKey KeyB() =>
        new("dual", null, null, ExecutionProviderKind.Cpu, "hashB", 0, "decoder") { EstimatedVramMb = 32 };

    [Fact]
    public async Task GetLeaseBundleAsync_AcquiresAllGraphs()
    {
        using var pool = new InferenceSessionPool(maxSessions: 4);
        using SessionLeaseBundle bundle = await pool.GetLeaseBundleAsync(
            [
                new SessionLeaseRequest(KeyA(), _ => Task.FromResult(CreateMinimalSession())),
                new SessionLeaseRequest(KeyB(), _ => Task.FromResult(CreateMinimalSession())),
            ],
            CancellationToken.None);

        Assert.Equal(2, bundle.Count);
        Assert.NotNull(bundle[0]);
        Assert.NotNull(bundle[1]);
    }

    [Fact]
    public async Task GetLeaseBundleAsync_DuplicateKeys_Throws()
    {
        using var pool = new InferenceSessionPool(maxSessions: 4);
        await Assert.ThrowsAsync<ArgumentException>(() => pool.GetLeaseBundleAsync(
            [
                new SessionLeaseRequest(KeyA(), _ => Task.FromResult(CreateMinimalSession())),
                new SessionLeaseRequest(KeyA(), _ => Task.FromResult(CreateMinimalSession())),
            ],
            CancellationToken.None));
    }

    [Fact]
    public async Task GetLeaseBundleAsync_OpposingCallerOrders_DoNotDeadlock()
    {
        using var pool = new InferenceSessionPool(maxSessions: 8);
        SessionLeaseRequest a = new(KeyA(), _ => Task.FromResult(CreateMinimalSession()));
        SessionLeaseRequest b = new(KeyB(), _ => Task.FromResult(CreateMinimalSession()));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Same graphs are exclusive, so bundles serialize — the point is that reversed
        // caller order still completes (stable sort) instead of deadlocking half-held.
        Task<SessionLeaseBundle> t1 = Task.Run(
            () => pool.GetLeaseBundleAsync([a, b], cts.Token), cts.Token);
        Task<SessionLeaseBundle> t2 = Task.Run(
            () => pool.GetLeaseBundleAsync([b, a], cts.Token), cts.Token);

        Task<SessionLeaseBundle> first = await Task.WhenAny(t1, t2).WaitAsync(cts.Token);
        SessionLeaseBundle firstBundle = await first.WaitAsync(cts.Token);
        firstBundle.Dispose();

        Task<SessionLeaseBundle> second = ReferenceEquals(first, t1) ? t2 : t1;
        SessionLeaseBundle secondBundle = await second.WaitAsync(cts.Token);
        secondBundle.Dispose();
    }

    [Fact]
    public async Task GetLeaseBundleAsync_DoesNotHoldFirstGraphWhileBlockedOnSecond()
    {
        using var pool = new InferenceSessionPool(maxSessions: 8);
        SessionLeaseRequest a = new(KeyA(), _ => Task.FromResult(CreateMinimalSession()));
        SessionLeaseRequest b = new(KeyB(), _ => Task.FromResult(CreateMinimalSession()));

        // Warm both graphs so phase-2 is pure gate acquire.
        using (await pool.GetLeaseBundleAsync([a, b], CancellationToken.None))
        {
        }

        // Hold graph A exclusively.
        SessionLease holdA = await pool.GetLeaseAsync(KeyA(), _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetLeaseBundleAsync([a, b], cts.Token));

        // Graph B must still be acquirable: the failed bundle did not keep B while stuck on A.
        using SessionLease takeB = await pool.GetLeaseAsync(
            KeyB(),
            _ => Task.FromResult(CreateMinimalSession()),
            CancellationToken.None);
        Assert.NotNull(takeB.Session);
        holdA.Dispose();
    }

    // ── TrimToVramBudgetAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task TrimToVramBudgetAsync_AfterDispose_Throws()
    {
        var pool = new InferenceSessionPool(4);
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.TrimToVramBudgetAsync(1024));
    }

    [Fact]
    public async Task TrimToVramBudgetAsync_NegativeTarget_Throws()
    {
        using var pool = new InferenceSessionPool(4);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => pool.TrimToVramBudgetAsync(-1));
    }

    [Fact]
    public async Task TrimToVramBudgetAsync_EmptyPool_ReturnsZero()
    {
        using var pool = new InferenceSessionPool(4);

        int evicted = await pool.TrimToVramBudgetAsync(0);

        Assert.Equal(0, evicted);
    }

    [Fact]
    public async Task TrimToVramBudgetAsync_BudgetNotExceeded_EvictsNothing()
    {
        using var pool = new InferenceSessionPool(4);
        var key = new SessionPoolKey("eng", "m", null, ExecutionProviderKind.Cpu, "h1", 0, "default")
        {
            EstimatedVramMb = 500
        };

        // Add and release the session so it's idle
        using (await pool.GetLeaseAsync(key, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }

        // Budget is well above the session's footprint
        int evicted = await pool.TrimToVramBudgetAsync(1000);

        Assert.Equal(0, evicted);
    }

    [Fact]
    public async Task TrimToVramBudgetAsync_BudgetExceeded_EvictsLruIdleSession()
    {
        using var pool = new InferenceSessionPool(4);
        var key1 = new SessionPoolKey("eng", "m", null, ExecutionProviderKind.Cpu, "h1", 0, "default")
        {
            EstimatedVramMb = 600
        };
        var key2 = new SessionPoolKey("eng", "m2", null, ExecutionProviderKind.Cpu, "h2", 0, "default")
        {
            EstimatedVramMb = 600
        };

        using (await pool.GetLeaseAsync(key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }
        using (await pool.GetLeaseAsync(key2, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None)) { }

        // Total 1200 MB; trim to 700 MB — should evict exactly 1 session (the LRU one)
        int evicted = await pool.TrimToVramBudgetAsync(700);

        Assert.Equal(1, evicted);
    }

    [Fact]
    public async Task TrimToVramBudgetAsync_DoesNotEvictLeasedSession()
    {
        using var pool = new InferenceSessionPool(4);
        var key = new SessionPoolKey("eng", "m", null, ExecutionProviderKind.Cpu, "h1", 0, "default")
        {
            EstimatedVramMb = 2000
        };

        // Hold the lease while we try to trim
        using (await pool.GetLeaseAsync(key, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None))
        {
            int evicted = await pool.TrimToVramBudgetAsync(0);

            // Session is leased; cannot be evicted
            Assert.Equal(0, evicted);
        }
    }

    // ── SharedPoolOptions (production activation contract) ────────────────────

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("on", true)]
    [InlineData("enabled", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("off", false)]
    [InlineData("disabled", false)]
    [InlineData("", true)]
    [InlineData("yes", true)]
    [InlineData("2", true)]
    [InlineData("typpo", true)]
    public void SharedPoolOptions_AdmissionFlag_OnlyExplicitNegativeDisables(string raw, bool expected)
    {
        // Admission is the safe default: unset, unknown, and typo values keep it enabled;
        // only an explicit negative token turns it off.
        Assert.Equal(expected, SharedPoolOptions.ParseAdmissionFlag(raw));
    }

    [Fact]
    public void SharedPoolOptions_AdmissionFlag_UnsetStaysOn()
    {
        using var env = new ScopedEnvironment();
        env.Clear(SharedPoolOptions.AdmissionVariable);

        Assert.True(SharedPoolOptions.ReadAdmissionFlag(SharedPoolOptions.AdmissionVariable));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("4", 4)]
    [InlineData(" 20 ", 20)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    [InlineData("12.5", null)]
    [InlineData("3000000000", null)]            // exceeds int.MaxValue — falls back
    [InlineData("9223372036854775807", null)]   // long.MaxValue — falls back, never throws
    public void SharedPoolOptions_PositiveInt32_RejectsUnsetInvalidAndOverflow(string? raw, int? expected)
    {
        Assert.Equal(expected, SharedPoolOptions.ParsePositiveInt32(raw));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("4096", 4096L)]
    [InlineData(" 8192 ", 8192L)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    [InlineData("12.5", null)]
    public void SharedPoolOptions_PositiveInt64_RejectsUnsetAndInvalid(string? raw, long? expected)
    {
        Assert.Equal(expected, SharedPoolOptions.ParsePositiveInt64(raw));
    }

    [Fact]
    public void SharedPoolOptions_BudgetAndCapacity_AreReadFromEnvironment()
    {
        using var env = new ScopedEnvironment();
        env.Set(SharedPoolOptions.BudgetMbVariable, "8192");
        env.Set(SharedPoolOptions.HostBudgetMbVariable, "2048");
        env.Set(SharedPoolOptions.MaxSessionsVariable, "20");
        env.Set(SharedPoolOptions.AdmissionVariable, "0");

        Assert.False(SharedPoolOptions.ReadAdmissionFlag(SharedPoolOptions.AdmissionVariable));
        Assert.Equal(8192L, SharedPoolOptions.ReadPositiveInt64(SharedPoolOptions.BudgetMbVariable));
        Assert.Equal(2048L, SharedPoolOptions.ReadPositiveInt64(SharedPoolOptions.HostBudgetMbVariable));
        Assert.Equal(20, SharedPoolOptions.ReadPositiveInt32(SharedPoolOptions.MaxSessionsVariable));
    }

    [Fact]
    public void SharedPoolOptions_Defaults_AreSafeOnAndMatchPoolDefaults()
    {
        // With no environment set, admission is hard-on with the pool's documented
        // budgets and capacity.
        using var env = new ScopedEnvironment();
        env.Clear(SharedPoolOptions.AdmissionVariable);
        env.Clear(SharedPoolOptions.BudgetMbVariable);
        env.Clear(SharedPoolOptions.HostBudgetMbVariable);
        env.Clear(SharedPoolOptions.MaxSessionsVariable);

        Assert.True(SharedPoolOptions.ReadAdmissionFlag(SharedPoolOptions.AdmissionVariable));
        Assert.Null(SharedPoolOptions.ReadPositiveInt64(SharedPoolOptions.BudgetMbVariable));
        Assert.Null(SharedPoolOptions.ReadPositiveInt64(SharedPoolOptions.HostBudgetMbVariable));
        Assert.Null(SharedPoolOptions.ReadPositiveInt32(SharedPoolOptions.MaxSessionsVariable));
        Assert.Equal(12, InferenceSessionPool.DefaultMaxSessions);
        Assert.Equal(4096L, InferenceSessionPool.DefaultMemoryBudgetMb);
        Assert.Equal(4096L, InferenceSessionPool.DefaultHostMemoryBudgetMb);
    }

    [Fact]
    public void Shared_IsStable_AcrossAccesses()
    {
        // Shared is now lazily built; every consumer must observe the same instance.
        Assert.Same(InferenceSessionPool.Shared, InferenceSessionPool.Shared);
    }

    /// <summary>
    /// Sets an environment variable for the duration of a test and restores it afterwards,
    /// so option parsing can be exercised without leaking state across the test run.
    /// </summary>
    private sealed class ScopedEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> original = new();

        public void Set(string variable, string? value) => SetCore(variable, value);

        public void Clear(string variable) => SetCore(variable, null);

        public void Dispose()
        {
            foreach ((string variable, string? value) in original)
            {
                Environment.SetEnvironmentVariable(variable, value);
            }
        }

        private void SetCore(string variable, string? value)
        {
            if (!original.ContainsKey(variable))
            {
                original[variable] = Environment.GetEnvironmentVariable(variable);
            }

            Environment.SetEnvironmentVariable(variable, value);
        }
    }

    // ── Hard admission defaults & host/accelerator buckets ──────────────────

    private static SessionPoolKey AcceleratorKey(string hash, long mb, int deviceId = 0, ExecutionProviderKind provider = ExecutionProviderKind.DirectMl) =>
        new("eng", null, null, provider, hash, deviceId, "default") { EstimatedVramMb = mb };

    private static SessionPoolKey HostKey(string hash, long mb, ExecutionProviderKind provider = ExecutionProviderKind.Cpu) =>
        new("eng", null, null, provider, hash, null, "default") { EstimatedVramMb = mb };

    [Fact]
    public async Task DefaultConstructor_HardAdmission_BlocksAcceleratorBeyondDefaultBudget()
    {
        // Defaults: admission on, 4096 MB accelerator budget per device. A second DML
        // graph that cannot fit must wait — never fall back to an ephemeral session.
        using var pool = new InferenceSessionPool();
        var key1 = AcceleratorKey("ha1", 3000);
        var key2 = AcceleratorKey("ha2", 2000);
        int key2FactoryCalls = 0;

        using SessionLease lease1 = await pool.GetLeaseAsync(
            key1, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.GetLeaseAsync(
            key2,
            _ => { key2FactoryCalls++; return Task.FromResult(CreateMinimalSession()); },
            cts.Token));

        Assert.Equal(0, key2FactoryCalls);
    }

    [Fact]
    public async Task AdmissionBuckets_HostRamAndAcceleratorDevice0_AreSeparate()
    {
        // CPU sessions no longer collide with accelerator device 0: each has its own budget.
        using var pool = new InferenceSessionPool(
            maxSessions: 8, memoryBudgetMb: 4096, hostMemoryBudgetMb: 4096);
        var cpuKey = HostKey("hs1", 3000);
        var dmlKey = AcceleratorKey("hs2", 3000);

        // Host RAM held does not block accelerator admission.
        using (SessionLease cpuLease = await pool.GetLeaseAsync(
                   cpuKey, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None))
        using (SessionLease dmlLease = await pool.GetLeaseAsync(
                   dmlKey, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None))
        {
            Assert.NotNull(dmlLease.Session);
        }

        // Accelerator device 0 held does not block host admission either.
        using SessionLease heldDml = await pool.GetLeaseAsync(
            AcceleratorKey("hs3", 3000), _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);
        using SessionLease cpu2 = await pool.GetLeaseAsync(
            HostKey("hs4", 3000), _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);
        Assert.NotNull(cpu2.Session);
    }

    [Fact]
    public async Task AdmissionBuckets_CpuAndDnnl_ShareHostRamBudget()
    {
        using var pool = new InferenceSessionPool(
            maxSessions: 8, hostMemoryBudgetMb: 100);
        var cpuKey = HostKey("hc1", 80, ExecutionProviderKind.Cpu);
        var dnnlKey = HostKey("hc2", 80, ExecutionProviderKind.Dnnl);

        using SessionLease lease1 = await pool.GetLeaseAsync(
            cpuKey, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);

        // CPU and DNNL draw from the same host RAM budget: 80 + 80 > 100 waits.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetLeaseAsync(dnnlKey, _ => Task.FromResult(CreateMinimalSession()), cts.Token));

        // After release the DNNL session fits in the freed host budget.
        lease1.Dispose();
        using SessionLease lease2 = await pool.GetLeaseAsync(
            dnnlKey, _ => Task.FromResult(CreateMinimalSession()), CancellationToken.None);
        Assert.NotNull(lease2.Session);
    }

    // ── Bundle hard admission ───────────────────────────────────────────────

    [Fact]
    public async Task GetLeaseBundleAsync_AggregateOversize_FailsBeforeAnyFactory()
    {
        using var pool = new InferenceSessionPool();
        var big1 = AcceleratorKey("bo1", 3000);
        var big2 = AcceleratorKey("bo2", 2000);
        int factoryCalls = 0;

        Task<InferenceSession> Factory(CancellationToken _)
        {
            factoryCalls++;
            return Task.FromResult(CreateMinimalSession());
        }

        // 3000 + 2000 exceeds the default 4096 MB device-0 accelerator budget.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.GetLeaseBundleAsync(
                [new SessionLeaseRequest(big1, Factory), new SessionLeaseRequest(big2, Factory)],
                CancellationToken.None));

        Assert.Equal(0, factoryCalls);
        Assert.Contains("accelerator device 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLeaseBundleAsync_PreparationFailure_ReleasesPins()
    {
        using var pool = new InferenceSessionPool(
            maxSessions: 8, hostMemoryBudgetMb: 100);
        // Sorted order puts "aaaa" first: its residency pin is held when the second
        // member's factory fails. Cleanup must release the pin so idle eviction can
        // reclaim the host budget it held.
        var pinned = HostKey("aaaa", 80);
        var failing = HostKey("bbbb", 10);
        var probe = HostKey("cccc", 80);
        int probeCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.GetLeaseBundleAsync(
                [new SessionLeaseRequest(pinned, _ => Task.FromResult(CreateMinimalSession())),
                 new SessionLeaseRequest(failing,
                     _ => Task.FromException<InferenceSession>(new InvalidOperationException("synthetic create failure")))],
                CancellationToken.None));

        // The probe fits the 100MB host budget only if the failed preparation released
        // the pinned 80MB entry, letting idle eviction make room for it.
        using SessionLease lease = await pool.GetLeaseAsync(
            probe,
            _ => { probeCalls++; return Task.FromResult(CreateMinimalSession()); },
            CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, probeCalls);
        Assert.NotNull(lease.Session);
    }

    [Fact]
    public async Task GetLeaseBundleAsync_CountMode_CancellationDuringPreparation_DoesNotHang()
    {
        // Count mode never publishes an ephemeral warm, so residency-style preparation
        // would spin forever; the bundle must stay on the bounded warm path and honour
        // the caller's cancellation token.
        using var pool = new InferenceSessionPool(
            maxSessions: 1, enableMemoryAdmission: false);
        SessionLeaseRequest a = new(AcceleratorKey("cm1", 64), _ => Task.FromResult(CreateMinimalSession()));
        SessionLeaseRequest b = new(AcceleratorKey("cm2", 64), _ => Task.FromResult(CreateMinimalSession()));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetLeaseBundleAsync([a, b], cts.Token));
    }

    // ── RecommendedMaxSessions ────────────────────────────────────────────────

    [Fact]
    public void RecommendedMaxSessions_NullDevices_ReturnsDefault()
    {
        int result = InferenceSessionPool.RecommendedMaxSessions(null);
        Assert.Equal(12, result);
    }

    [Fact]
    public void RecommendedMaxSessions_NoGpu_ReturnsDefault()
    {
        var devices = new List<DeviceEntry>
        {
            new(DeviceKind.Cpu, 0, "CPU", "System", 0, 0, [ExecutionProviderKind.Cpu])
        };

        int result = InferenceSessionPool.RecommendedMaxSessions(devices);
        Assert.Equal(12, result);
    }

    [Fact]
    public void RecommendedMaxSessions_NvidiaGpuBelow8Gb_ReturnsDefault()
    {
        var devices = new List<DeviceEntry>
        {
            new(DeviceKind.DiscreteGpu, 0, "NVIDIA RTX 3060", "NVIDIA", 6144, 0, [ExecutionProviderKind.DirectMl])
        };

        int result = InferenceSessionPool.RecommendedMaxSessions(devices);
        Assert.Equal(12, result);
    }

    [Fact]
    public void RecommendedMaxSessions_NvidiaGpuAtLeast8Gb_Returns24()
    {
        var devices = new List<DeviceEntry>
        {
            new(DeviceKind.DiscreteGpu, 0, "NVIDIA RTX 4080", "NVIDIA", 16384, 0, [ExecutionProviderKind.DirectMl])
        };

        int result = InferenceSessionPool.RecommendedMaxSessions(devices);
        Assert.Equal(24, result);
    }

    [Fact]
    public void RecommendedMaxSessions_NonNvidiaGpuWith16Gb_ReturnsDefault()
    {
        var devices = new List<DeviceEntry>
        {
            new(DeviceKind.DiscreteGpu, 0, "AMD Radeon RX 7900 XTX", "AMD", 24576, 0, [ExecutionProviderKind.DirectMl])
        };

        int result = InferenceSessionPool.RecommendedMaxSessions(devices);
        Assert.Equal(12, result);
    }

    // ── Smoke ↔ stage pool-key identity ──────────────────────────────────────

    [Fact]
    public void SessionPoolKey_ModelIdAndVariant_participate_in_identity()
    {
        // Smoke and stage must build identical keys or smoke cannot warm the stage session.
        var smokeStyle = SessionPoolKey.ForEncoder(
            "nemotron-asr",
            @"C:\models\encoder.onnx",
            ExecutionProviderKind.TensorRTRtx,
            modelId: "vendor/nemotron-asr",
            variant: "default");
        var stageStyle = SessionPoolKey.ForEncoder(
            "nemotron-asr",
            @"C:\models\encoder.onnx",
            ExecutionProviderKind.TensorRTRtx,
            modelId: "vendor/nemotron-asr",
            variant: "default");
        var omittedIdentity = SessionPoolKey.ForEncoder(
            "nemotron-asr",
            @"C:\models\encoder.onnx",
            ExecutionProviderKind.TensorRTRtx);

        Assert.Equal(smokeStyle, stageStyle);
        Assert.NotEqual(smokeStyle, omittedIdentity);
    }

    [Fact]
    public void SessionPoolKey_Qwen3OmittingIdentity_matches_across_smoke_and_stage()
    {
        // Qwen3 production and smoke both omit modelId/variant — that pair must stay aligned.
        var smoke = SessionPoolKey.ForEncoder(
            "qwen3-asr",
            @"C:\models\encoder.onnx",
            ExecutionProviderKind.Cpu);
        var stage = SessionPoolKey.ForEncoder(
            "qwen3-asr",
            @"C:\models\encoder.onnx",
            ExecutionProviderKind.Cpu,
            modelId: null,
            variant: null);

        Assert.Equal(smoke, stage);
    }

    // ── Helper: build a minimal real InferenceSession from the ONNX operator ──

    /// <summary>
    /// Creates a minimal ONNX identity model in-memory so tests can obtain a real
    /// <see cref="InferenceSession"/> without loading any file from disk.
    /// The session accepts a single float input named "x" and returns it as "y".
    /// </summary>
    private static InferenceSession CreateMinimalSession()
    {
        // Build the smallest valid ONNX protobuf by hand (bytes match the official wire format).
        // Graph: x (float32 [1]) → Identity → y (float32 [1])
        byte[] model = BuildIdentityOnnxModel();
        return new InferenceSession(model);
    }

    private static byte[] BuildIdentityOnnxModel()
    {
        // Minimal ONNX model (ir_version=7, opset 9, Identity op).
        // ModelProto: ir_version=7, opset_imports=[{domain:"", version:9}],
        // graph={node=[{input:x, output:y, op_type:Identity}], input=[x:float[1]], output=[y:float[1]]}
        // Generated via: onnx.helper.make_model / SerializeToString
        return
        [
            0x08, 0x07, 0x3A, 0x3A, 0x0A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x01, 0x79, 0x22, 0x08,
            0x49, 0x64, 0x65, 0x6E, 0x74, 0x69, 0x74, 0x79, 0x12, 0x04, 0x74, 0x65, 0x73, 0x74,
            0x5A, 0x0F, 0x0A, 0x01, 0x78, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01, 0x12, 0x04, 0x0A,
            0x02, 0x08, 0x01, 0x62, 0x0F, 0x0A, 0x01, 0x79, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01,
            0x12, 0x04, 0x0A, 0x02, 0x08, 0x01, 0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
        ];
    }
}
