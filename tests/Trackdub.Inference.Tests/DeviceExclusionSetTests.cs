using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceExclusionSet"/> thread safety and correctness.
/// Validates Requirements 13.1, 13.2.
/// </summary>
public sealed class DeviceExclusionSetTests
{
    // ── Concurrent marking ────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentMarking_AllDevicesExcludedAfterCompletion()
    {
        var exclusionSet = new DeviceExclusionSet();
        const int deviceCount = 100;

        var tasks = Enumerable.Range(0, deviceCount).Select(i =>
            Task.Run(() =>
            {
                if (i % 2 == 0)
                    exclusionSet.MarkMemoryExhausted(i);
                else
                    exclusionSet.MarkFailed(i, $"Device {i} failed");
            }));

        await Task.WhenAll(tasks);

        for (int i = 0; i < deviceCount; i++)
        {
            Assert.True(exclusionSet.IsExcluded(i), $"Device {i} should be excluded after concurrent marking.");
        }
    }

    // ── Concurrent read/write ─────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentReadWrite_NoExceptionsAndConsistentState()
    {
        var exclusionSet = new DeviceExclusionSet();
        const int iterations = 1000;
        var exceptions = new List<Exception>();

        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                try
                {
                    exclusionSet.MarkMemoryExhausted(i % 50);
                    exclusionSet.MarkFailed(i % 50, "fail");
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }
        });

        var readerTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                try
                {
                    // IsExcluded should never throw regardless of concurrent writes
                    _ = exclusionSet.IsExcluded(i % 50);
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(writerTask, readerTask);

        Assert.Empty(exceptions);

        // After all writes complete, marked devices should be excluded
        for (int i = 0; i < 50; i++)
        {
            Assert.True(exclusionSet.IsExcluded(i), $"Device {i} should be excluded after writes complete.");
        }
    }

    // ── ClearRunExclusions ────────────────────────────────────────────────────

    [Fact]
    public void ClearRunExclusions_ResetsAllExclusions()
    {
        var exclusionSet = new DeviceExclusionSet();

        exclusionSet.MarkMemoryExhausted(0);
        exclusionSet.MarkMemoryExhausted(1);
        exclusionSet.MarkFailed(2, "driver crash");
        exclusionSet.MarkFailed(3, "timeout");

        // Verify all are excluded before clear
        Assert.True(exclusionSet.IsExcluded(0));
        Assert.True(exclusionSet.IsExcluded(1));
        Assert.True(exclusionSet.IsExcluded(2));
        Assert.True(exclusionSet.IsExcluded(3));

        exclusionSet.ClearRunExclusions();

        // All should be cleared
        Assert.False(exclusionSet.IsExcluded(0));
        Assert.False(exclusionSet.IsExcluded(1));
        Assert.False(exclusionSet.IsExcluded(2));
        Assert.False(exclusionSet.IsExcluded(3));
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public void MarkMemoryExhausted_Twice_DoesNotThrow()
    {
        var exclusionSet = new DeviceExclusionSet();

        exclusionSet.MarkMemoryExhausted(5);
        exclusionSet.MarkMemoryExhausted(5); // idempotent, no exception

        Assert.True(exclusionSet.IsExcluded(5));
    }

    [Fact]
    public void MarkFailed_Twice_DoesNotThrow()
    {
        var exclusionSet = new DeviceExclusionSet();

        exclusionSet.MarkFailed(7, "first failure");
        exclusionSet.MarkFailed(7, "second failure"); // idempotent, no exception

        Assert.True(exclusionSet.IsExcluded(7));
    }

    [Fact]
    public void MarkMemoryExhausted_ThenMarkFailed_SameDevice_DoesNotThrow()
    {
        var exclusionSet = new DeviceExclusionSet();

        exclusionSet.MarkMemoryExhausted(3);
        exclusionSet.MarkFailed(3, "also failed"); // idempotent, no exception

        Assert.True(exclusionSet.IsExcluded(3));
    }

    // ── MarkFailed vs MarkMemoryExhausted ─────────────────────────────────────

    [Fact]
    public void MarkFailed_ResultsInIsExcludedTrue()
    {
        var exclusionSet = new DeviceExclusionSet();

        exclusionSet.MarkFailed(4, "device removed");

        Assert.True(exclusionSet.IsExcluded(4));
    }

    [Fact]
    public void MarkMemoryExhausted_ResultsInIsExcludedTrue()
    {
        var exclusionSet = new DeviceExclusionSet();

        exclusionSet.MarkMemoryExhausted(6);

        Assert.True(exclusionSet.IsExcluded(6));
    }

    [Fact]
    public void IsExcluded_UnmarkedDevice_ReturnsFalse()
    {
        var exclusionSet = new DeviceExclusionSet();

        Assert.False(exclusionSet.IsExcluded(99));
    }
}
