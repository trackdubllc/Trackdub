using System.Threading.Channels;
using Trackdub.Application.Pipeline;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Xunit;

namespace Trackdub.Application.Tests.Pipeline;

public class BoundedPipelineChannelTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    private static PipelineStreamIdentity Id(
        int segment, long sequence, Guid? revision = null) =>
        new(Guid.NewGuid(), "snap-1", RuntimeStage.Asr, segment, revision, sequence);

    private static PipelineStreamItem<string> Item(
        int segment,
        long sequence,
        int bytes = 8,
        string? payload = null,
        Guid? revision = null) =>
        new(Id(segment, sequence, revision), payload ?? $"payload-{segment}", bytes);

    [Fact]
    public void StreamItemFactory_CreateTranslation_BuildsValidatedIdentity()
    {
        Guid runId = Guid.NewGuid();
        Guid revision = Guid.NewGuid();
        var segment = new TranslatedTextSegment(3, 1.5, 2.5, "héllo");

        PipelineStreamItem<TranslatedTextSegment> item = PipelineStreamItemFactory.CreateTranslation(
            segment, runId, "  snap-9  ", revision, 7);

        Assert.Equal(runId, item.Identity.RunId);
        Assert.Equal("snap-9", item.Identity.SnapshotId); // trimmed
        Assert.Equal(RuntimeStage.Translation, item.Identity.Stage);
        Assert.Equal(3, item.Identity.SegmentIndex);
        Assert.Equal(revision, item.Identity.RevisionId);
        Assert.Equal(7, item.Identity.Sequence);
        Assert.Same(segment, item.Payload);

        // "héllo" = 6 UTF-8 bytes + 64 fixed overhead
        Assert.Equal(70, item.EstimatedBytes);
    }

    [Fact]
    public void StreamItemFactory_CreateTranslation_RejectsInvalidContext()
    {
        var segment = new TranslatedTextSegment(0, 0, 1, "x");
        Assert.Throws<ArgumentException>(() =>
            PipelineStreamItemFactory.CreateTranslation(segment, Guid.Empty, "s", Guid.NewGuid(), 0));
        Assert.Throws<ArgumentException>(() =>
            PipelineStreamItemFactory.CreateTranslation(segment, Guid.NewGuid(), " ", Guid.NewGuid(), 0));
        Assert.Throws<ArgumentException>(() =>
            PipelineStreamItemFactory.CreateTranslation(segment, Guid.NewGuid(), "s", Guid.Empty, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PipelineStreamItemFactory.CreateTranslation(segment, Guid.NewGuid(), "s", Guid.NewGuid(), -1));
        Assert.Throws<ArgumentNullException>(() =>
            PipelineStreamItemFactory.CreateTranslation(null!, Guid.NewGuid(), "s", Guid.NewGuid(), 0));
    }

    [Fact]
    public async Task Options_And_WriteAsync_ValidateBeforeAccounting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedPipelineChannelOptions(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedPipelineChannelOptions(1, 0));

        await using var channel = new BoundedPipelineChannel<string>(new(4, 100));

        await Assert.ThrowsAsync<ArgumentException>(() => channel.WriteAsync(
            new PipelineStreamItem<string>(Id(0, 0) with { RunId = Guid.Empty }, "x", 8),
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => channel.WriteAsync(
            new PipelineStreamItem<string>(Id(0, 0) with { SnapshotId = " " }, "x", 8),
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => channel.WriteAsync(
            new PipelineStreamItem<string>(Id(-1, 0), "x", 8),
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => channel.WriteAsync(
            new PipelineStreamItem<string>(Id(0, -1), "x", 8),
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => channel.WriteAsync(
            new PipelineStreamItem<string>(Id(0, 0), "x", 0),
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => channel.WriteAsync(
            new PipelineStreamItem<string>(Id(0, 0), null!, 8),
            TestContext.Current.CancellationToken).AsTask());

        // Oversize is rejected before touching channel or byte budget.
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.WriteAsync(
            new PipelineStreamItem<string>(Id(0, 0), "x", 101),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, channel.BufferedItems);
        Assert.Equal(0, channel.BufferedBytes);
        Assert.Equal(0, channel.PeakBufferedItems);
        Assert.Equal(0, channel.PeakBufferedBytes);
    }

    [Fact]
    public async Task WriteAsync_ItemCapacityBlocksSecondWriter_UntilReaderAdvances()
    {
        await using var channel = new BoundedPipelineChannel<string>(new(itemCapacity: 1, byteCapacity: 1000));

        await channel.WriteAsync(Item(0, 0), TestContext.Current.CancellationToken);
        Assert.Equal(1, channel.BufferedItems);
        Assert.Equal(8, channel.BufferedBytes);

        // Second writer reserves its bytes, then blocks on the full item slot.
        Task secondWrite = channel.WriteAsync(Item(1, 1), TestContext.Current.CancellationToken).AsTask();
        Assert.True(SpinWait.SpinUntil(() => channel.BufferedBytes == 16, TimeSpan.FromSeconds(5)));
        Assert.False(secondWrite.IsCompleted);

        IAsyncEnumerator<PipelineStreamItem<string>> reader =
            channel.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard, TestContext.Current.CancellationToken)); // holds item 0

        // The slot is free now — writer 2 lands even though item 0 is still held.
        await secondWrite.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.Equal(2, channel.BufferedItems);

        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard, TestContext.Current.CancellationToken)); // releases item 0, holds item 1
        Assert.Equal(1, channel.BufferedItems);
        Assert.Equal(8, channel.BufferedBytes);

        await reader.DisposeAsync();
        Assert.Equal(0, channel.BufferedItems);
        Assert.Equal(0, channel.BufferedBytes);
    }

    [Fact]
    public async Task WriteAsync_ByteBudgetBlocks_WhileConsumerHoldsYieldedItem()
    {
        await using var channel = new BoundedPipelineChannel<string>(new(itemCapacity: 4, byteCapacity: 16));

        await channel.WriteAsync(Item(0, 0), TestContext.Current.CancellationToken);
        await channel.WriteAsync(Item(1, 1), TestContext.Current.CancellationToken); // byte budget now full (16/16), slots remain

        Task thirdWrite = channel.WriteAsync(Item(2, 2), TestContext.Current.CancellationToken).AsTask();
        Assert.True(SpinWait.SpinUntil(() => !thirdWrite.IsCompleted, TimeSpan.FromSeconds(5)));
        Assert.Equal(16, channel.BufferedBytes);

        IAsyncEnumerator<PipelineStreamItem<string>> reader =
            channel.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard, TestContext.Current.CancellationToken)); // pulls item 0, still holds its bytes

        // Accounting is consumer-held, not queue-held: writer 3 still blocked.
        Assert.True(SpinWait.SpinUntil(() => !thirdWrite.IsCompleted, TimeSpan.FromSeconds(5)));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(thirdWrite.IsCompleted);
        Assert.Equal(16, channel.BufferedBytes);

        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard, TestContext.Current.CancellationToken)); // advances past item 0
        await thirdWrite.WaitAsync(HangGuard, TestContext.Current.CancellationToken); // freed bytes let item 2 land
        Assert.Equal(2, channel.BufferedItems); // item 1 consumer-held + item 2 queued
        Assert.Equal(16, channel.BufferedBytes);

        await reader.DisposeAsync();
    }

    [Fact]
    public async Task ReadAllAsync_PreservesOrderingAndIdentity()
    {
        await using var channel = new BoundedPipelineChannel<string>(new(8, 1024));
        Guid revision = Guid.NewGuid();
        PipelineStreamItem<string>[] items =
        [
            Item(0, 0, bytes: 4, payload: "first", revision: revision),
            Item(1, 1, bytes: 4, payload: "second", revision: revision),
            Item(2, 2, bytes: 4, payload: "third"),
        ];
        foreach (PipelineStreamItem<string> item in items)
        {
            await channel.WriteAsync(item, TestContext.Current.CancellationToken);
        }

        Assert.True(channel.TryComplete());

        List<PipelineStreamItem<string>> received = [];
        await foreach (PipelineStreamItem<string> item in channel.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            received.Add(item);
        }

        Assert.Equal(items, received);
        Assert.Equal(0, channel.BufferedItems);
        Assert.Equal(0, channel.BufferedBytes);
    }

    [Fact]
    public async Task WriteAsync_CancelledBudgetWaiter_LeavesNoAccounting()
    {
        await using var channel = new BoundedPipelineChannel<string>(new(itemCapacity: 4, byteCapacity: 16));

        await channel.WriteAsync(Item(0, 0), TestContext.Current.CancellationToken);
        await channel.WriteAsync(Item(1, 1), TestContext.Current.CancellationToken); // budget full

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task blocked = channel.WriteAsync(Item(2, 2), cts.Token).AsTask();
        Assert.True(SpinWait.SpinUntil(() => !blocked.IsCompleted, TimeSpan.FromSeconds(5)));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);

        Assert.Equal(2, channel.BufferedItems);
        Assert.Equal(16, channel.BufferedBytes);
        Assert.Equal(16, channel.PeakBufferedBytes);
    }

    [Fact]
    public async Task RunAsync_ProducerFault_RethrowsOriginal_AfterConsumerObservesFault()
    {
        var consumerObservedFault = new TaskCompletionSource<Exception>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedPipelineRunner.RunAsync<string>(
                new(itemCapacity: 2, byteCapacity: 32),
                async (channel, _) =>
                {
                    await channel.WriteAsync(Item(0, 0), TestContext.Current.CancellationToken);
                    throw new InvalidOperationException("producer exploded");
                },
                async (items, ct) =>
                {
                    try
                    {
                        await foreach (PipelineStreamItem<string> _ in items.WithCancellation(ct))
                        {
                        }
                    }
                    catch (Exception e)
                    {
                        consumerObservedFault.TrySetResult(e);
                        throw;
                    }
                },
                TestContext.Current.CancellationToken).WaitAsync(HangGuard, TestContext.Current.CancellationToken));

        Assert.Equal("producer exploded", ex.Message);

        // Reader drains the buffered item, then observes the original completion fault.
        Exception observed = await consumerObservedFault.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal("producer exploded", observed.Message);
    }

    [Fact]
    public async Task RunAsync_ConsumerFault_CancelsAndJoinsBlockedProducer()
    {
        var producerCancelled = new TaskCompletionSource();
        var producerFinallyRan = new TaskCompletionSource();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedPipelineRunner.RunAsync<string>(
                new(itemCapacity: 1, byteCapacity: 8),
                async (channel, ct) =>
                {
                    try
                    {
                        // More items than can ever drain — at least one write is always
                        // blocked when the consumer faults.
                        for (int i = 0; i < 4; i++)
                        {
                            await channel.WriteAsync(Item(i, i), ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        producerCancelled.TrySetResult();
                        throw;
                    }
                    finally
                    {
                        producerFinallyRan.TrySetResult();
                    }
                },
                async (items, ct) =>
                {
                    IAsyncEnumerator<PipelineStreamItem<string>> reader =
                        items.GetAsyncEnumerator(ct);
                    await reader.MoveNextAsync(); // consume one, then fault
                    await reader.DisposeAsync();
                    throw new InvalidOperationException("consumer exploded");
                },
                TestContext.Current.CancellationToken).WaitAsync(HangGuard, TestContext.Current.CancellationToken));

        await producerCancelled.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        await producerFinallyRan.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_ConsumerEarlyReturn_JoinsBlockedProducer_WithoutFault()
    {
        var producerCancelled = new TaskCompletionSource();

        await BoundedPipelineRunner.RunAsync<string>(
            new(itemCapacity: 1, byteCapacity: 8),
            async (channel, ct) =>
            {
                try
                {
                    await channel.WriteAsync(Item(0, 0), ct);
                    await channel.WriteAsync(Item(1, 1), ct);
                }
                catch (OperationCanceledException)
                {
                    producerCancelled.TrySetResult();
                    throw;
                }
            },
            (items, _) =>
            {
                // Consumer returns immediately without reading.
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken).WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        await producerCancelled.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_CancelsAndJoinsBothWorkers()
    {
        var producerObserved = new TaskCompletionSource();
        var consumerObserved = new TaskCompletionSource();
        var never = new TaskCompletionSource();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task run = BoundedPipelineRunner.RunAsync<string>(
            new(itemCapacity: 1, byteCapacity: 8),
            async (channel, ct) =>
            {
                try
                {
                    await channel.WriteAsync(Item(0, 0), ct);
                    await never.Task.WaitAsync(ct); // parked mid-stream
                }
                finally
                {
                    producerObserved.TrySetResult();
                }
            },
            async (items, ct) =>
            {
                IAsyncEnumerator<PipelineStreamItem<string>> reader = items.GetAsyncEnumerator(ct);
                try
                {
                    await reader.MoveNextAsync(); // take item 0
                    await never.Task.WaitAsync(ct); // parked mid-consume
                }
                finally
                {
                    // Dispose the enumerator so the held item's accounting releases and
                    // the runner's channel disposal cannot wait forever.
                    await reader.DisposeAsync();
                    consumerObserved.TrySetResult();
                }
            },
            cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        await producerObserved.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        await consumerObserved.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReadAllAsync_ChannelFault_DrainsBufferedThenSurfacesOriginalError()
    {
        await using var channel = new BoundedPipelineChannel<string>(new(4, 100));
        await channel.WriteAsync(Item(0, 0), TestContext.Current.CancellationToken);
        var original = new InvalidOperationException("fatal");
        Assert.True(channel.TryComplete(original));
        Assert.False(channel.TryComplete()); // complete-writer-once

        List<string> seen = [];
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await foreach (PipelineStreamItem<string> item in channel.ReadAllAsync(TestContext.Current.CancellationToken))
                {
                    seen.Add(item.Payload);
                }
            });

        Assert.Same(original, thrown);
        Assert.Equal(["payload-0"], seen);
    }

    [Fact]
    public async Task WriteAsync_PublicationRace_ReaderAndDisposeWaitForCounted()
    {
        var atSeam = new TaskCompletionSource();
        var releaseSeam = new TaskCompletionSource();
        var channel = new BoundedPipelineChannel<string>(
            new(4, 100),
            afterWriteBeforeCount: async () =>
            {
                atSeam.TrySetResult();
                await releaseSeam.Task;
            });

        Task write = channel.WriteAsync(Item(0, 0), TestContext.Current.CancellationToken).AsTask();
        await atSeam.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        // Entry published, count not yet committed: bytes reserved, items still zero.
        Assert.Equal(8, channel.BufferedBytes);
        Assert.Equal(0, channel.BufferedItems);

        // Reader dequeues the entry but must wait on Counted before yielding/releasing.
        IAsyncEnumerator<PipelineStreamItem<string>> reader =
            channel.ReadAllAsync(TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Task<bool> firstRead = reader.MoveNextAsync().AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(firstRead.IsCompleted);

        // Concurrent dispose cannot complete while publication is mid-flight.
        Task dispose = channel.DisposeAsync().AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(dispose.IsCompleted);

        releaseSeam.SetResult();

        Assert.True(await firstRead.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.Equal("payload-0", reader.Current.Payload);
        await write.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        await reader.DisposeAsync();
        await dispose.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(0, channel.BufferedItems);
        Assert.Equal(0, channel.BufferedBytes);
    }

    [Fact]
    public async Task RunAsync_BothWorkersCompleted_ConsumerFault_ThrowsExactException()
    {
        var fault = new InvalidOperationException("consumer died first");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedPipelineRunner.RunAsync<string>(
                new(4, 64),
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromException(fault),
                TestContext.Current.CancellationToken)
                .WaitAsync(HangGuard, TestContext.Current.CancellationToken));

        Assert.Same(fault, ex);
    }

    [Fact]
    public async Task RunAsync_SynchronousConsumerThrow_CancelsAndJoinsBlockedProducer()
    {
        var producerCancelled = new TaskCompletionSource();
        var producerFinallyRan = new TaskCompletionSource();
        var fault = new InvalidOperationException("sync consumer throw");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedPipelineRunner.RunAsync<string>(
                new(itemCapacity: 1, byteCapacity: 8),
                async (channel, ct) =>
                {
                    try
                    {
                        await channel.WriteAsync(Item(0, 0), ct);
                        await channel.WriteAsync(Item(1, 1), ct); // blocked: slot + bytes full
                    }
                    catch (OperationCanceledException)
                    {
                        producerCancelled.TrySetResult();
                        throw;
                    }
                    finally
                    {
                        producerFinallyRan.TrySetResult();
                    }
                },
                (_, _) => throw fault, // synchronous delegate throw, captured by the wrapper
                TestContext.Current.CancellationToken)
                .WaitAsync(HangGuard, TestContext.Current.CancellationToken));

        Assert.Same(fault, ex);
        await producerCancelled.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        await producerFinallyRan.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_WorkerCancellationVsFault_NonCancellationFaultWins()
    {
        var fault = new InvalidOperationException("real fault racing cancellation");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await cts.CancelAsync(); // caller token already canceled

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedPipelineRunner.RunAsync<string>(
                new(4, 64),
                (_, ct) => Task.FromCanceled(ct),
                (_, _) => Task.FromException(fault),
                cts.Token)
                .WaitAsync(HangGuard, TestContext.Current.CancellationToken));

        // Diagnosable real fault beats both worker and caller cancellation.
        Assert.Same(fault, ex);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForConsumerHeldItem_ThenMetricsZero_AndIdempotent()
    {
        var channel = new BoundedPipelineChannel<string>(new(4, 100));
        await channel.WriteAsync(Item(0, 0), TestContext.Current.CancellationToken);

        IAsyncEnumerator<PipelineStreamItem<string>> reader =
            channel.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard, TestContext.Current.CancellationToken)); // holds item 0

        Task dispose = channel.DisposeAsync().AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(dispose.IsCompleted); // waits for the held item's accounting
        Assert.Equal(8, channel.BufferedBytes);

        await reader.DisposeAsync(); // releases item 0
        await dispose.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(0, channel.BufferedItems);
        Assert.Equal(0, channel.BufferedBytes);

        await channel.DisposeAsync(); // idempotent
    }
}
