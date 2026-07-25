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

    // ── LRU eviction & ephemeral fallback ────────────────────────────────────

    [Fact]
    public async Task LruEviction_EjectsOldestIdleEntry_WhenCapacityExceeded()
    {
        using var pool = new InferenceSessionPool(maxSessions: 2);

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
        using var pool = new InferenceSessionPool(maxSessions: 1);

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
        using var pool = new InferenceSessionPool(maxSessions: 2);

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
        using var pool = new InferenceSessionPool(maxSessions: 1);

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

    // ── RecommendedMaxSessions ────────────────────────────────────────────────

    [Fact]
    public void RecommendedMaxSessions_NullDevices_ReturnsDefault()
    {
        int result = InferenceSessionPool.RecommendedMaxSessions(null);
        Assert.Equal(8, result);
    }

    [Fact]
    public void RecommendedMaxSessions_NoGpu_ReturnsDefault()
    {
        var devices = new List<DeviceEntry>
        {
            new(DeviceKind.Cpu, 0, "CPU", "System", 0, 0, [ExecutionProviderKind.Cpu])
        };

        int result = InferenceSessionPool.RecommendedMaxSessions(devices);
        Assert.Equal(8, result);
    }

    [Fact]
    public void RecommendedMaxSessions_NvidiaGpuBelow8Gb_ReturnsDefault()
    {
        var devices = new List<DeviceEntry>
        {
            new(DeviceKind.DiscreteGpu, 0, "NVIDIA RTX 3060", "NVIDIA", 6144, 0, [ExecutionProviderKind.DirectMl])
        };

        int result = InferenceSessionPool.RecommendedMaxSessions(devices);
        Assert.Equal(8, result);
    }

    [Fact]
    public void RecommendedMaxSessions_NvidiaGpuAtLeast8Gb_Returns16()
    {
        var devices = new List<DeviceEntry>
        {
            new(DeviceKind.DiscreteGpu, 0, "NVIDIA RTX 4080", "NVIDIA", 16384, 0, [ExecutionProviderKind.DirectMl])
        };

        int result = InferenceSessionPool.RecommendedMaxSessions(devices);
        Assert.Equal(16, result);
    }

    [Fact]
    public void RecommendedMaxSessions_NonNvidiaGpuWith16Gb_ReturnsDefault()
    {
        var devices = new List<DeviceEntry>
        {
            new(DeviceKind.DiscreteGpu, 0, "AMD Radeon RX 7900 XTX", "AMD", 24576, 0, [ExecutionProviderKind.DirectMl])
        };

        int result = InferenceSessionPool.RecommendedMaxSessions(devices);
        Assert.Equal(8, result);
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
