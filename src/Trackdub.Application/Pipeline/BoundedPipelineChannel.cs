using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Pipeline;

/// <summary>Capacity for a <see cref="BoundedPipelineChannel{T}"/>: max buffered items and bytes.</summary>
public sealed record BoundedPipelineChannelOptions
{
    public BoundedPipelineChannelOptions(int itemCapacity, long byteCapacity)
    {
        if (itemCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCapacity), "Item capacity must be positive.");
        }

        if (byteCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCapacity), "Byte capacity must be positive.");
        }

        ItemCapacity = itemCapacity;
        ByteCapacity = byteCapacity;
    }

    public int ItemCapacity { get; }

    public long ByteCapacity { get; }
}

/// <summary>
/// Reusable bounded transport for <see cref="PipelineStreamItem{T}"/> units with item AND
/// weighted-byte backpressure, stable identity validation, fault completion, and joined
/// disposal. Foundation for streaming stage overlap — nothing in the production pipeline
/// consumes it yet.
/// </summary>
/// <remarks>
/// Invariants:
/// <list type="bullet">
/// <item>Bytes are reserved before the channel write and released exactly once on every
/// failure path; a canceled wait never consumes budget.</item>
/// <item>Entries are published to the channel before their item count increments — readers
/// and disposal therefore wait on <c>Entry.Counted</c> so a dequeue/release can never race
/// the publish and drive metrics negative.</item>
/// <item>An item's item+byte accounting is held from write until the consumer's enumerator
/// advances past (or is disposed past) that yielded item — so the bounds cover items held
/// by the consumer, not only the channel queue.</item>
/// <item><see cref="BufferedBytes"/> includes reservations of writers blocked waiting for
/// an item slot — intentionally conservative so admission pressure reflects queued work.</item>
/// <item>Completion wakes byte-budget waiters, which observe <see cref="ChannelClosedException"/>
/// carrying the completion error instead of hanging.</item>
/// <item>Disposal requires readers to dispose their enumerators; <see cref="DisposeAsync"/>
/// waits for outstanding accounting to reach zero.</item>
/// </list>
/// </remarks>
public sealed class BoundedPipelineChannel<T> : IAsyncDisposable
    where T : notnull
{
    /// <summary>
    /// A channel slot wrapper so item accounting is committed exactly once by the writer,
    /// after the write succeeds. Readers/drainers must observe <see cref="Counted"/> before
    /// releasing — dequeue can legally precede the increment.
    /// </summary>
    private sealed class Entry(PipelineStreamItem<T> item)
    {
        private readonly TaskCompletionSource counted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PipelineStreamItem<T> Item { get; } = item;

        internal Task Counted => counted.Task;

        internal void MarkCounted() => counted.TrySetResult();
    }

    private readonly BoundedPipelineChannelOptions options;
    private readonly Channel<Entry> channel;
    private readonly object sync = new();
    private readonly Func<Task>? afterWriteBeforeCount; // internal test seam for the publish race
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long bufferedBytes;
    private long peakBytes;
    private int bufferedItems;
    private int peakItems;
    private bool completed;
    private Exception? completionError;

    public BoundedPipelineChannel(BoundedPipelineChannelOptions options)
        : this(options, afterWriteBeforeCount: null)
    {
    }

    internal BoundedPipelineChannel(
        BoundedPipelineChannelOptions options,
        Func<Task>? afterWriteBeforeCount)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.afterWriteBeforeCount = afterWriteBeforeCount;
        channel = Channel.CreateBounded<Entry>(
            new BoundedChannelOptions(options.ItemCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = false,
            });
    }

    public int BufferedItems
    {
        get { lock (sync) { return bufferedItems; } }
    }

    public long BufferedBytes
    {
        get { lock (sync) { return bufferedBytes; } }
    }

    public int PeakBufferedItems
    {
        get { lock (sync) { return peakItems; } }
    }

    public long PeakBufferedBytes
    {
        get { lock (sync) { return peakBytes; } }
    }

    public async ValueTask WriteAsync(
        PipelineStreamItem<T> item,
        CancellationToken cancellationToken = default)
    {
        Validate(item);

        // Oversize rejection happens before touching channel or budget so a single
        // too-large item can never deadlock the transport.
        if (item.EstimatedBytes > options.ByteCapacity)
        {
            throw new InvalidOperationException(
                $"Stream item of {item.EstimatedBytes} bytes exceeds channel byte capacity "
                    + $"{options.ByteCapacity}.");
        }

        // Reserve bytes before writing: budget accounts for writers blocked on a full
        // item slot, keeping observed pressure honest. Cancellation here consumes nothing.
        while (true)
        {
            Task wait;
            bool reserved;
            lock (sync)
            {
                if (completed)
                {
                    throw new ChannelClosedException(completionError);
                }

                reserved = item.EstimatedBytes <= options.ByteCapacity - bufferedBytes;
                if (reserved)
                {
                    bufferedBytes += item.EstimatedBytes;
                    peakBytes = Math.Max(peakBytes, bufferedBytes);
                    wait = Task.CompletedTask;
                }
                else
                {
                    wait = signal.Task;
                }
            }

            if (reserved)
            {
                break;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var entry = new Entry(item);
        try
        {
            await channel.Writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseItem(item.EstimatedBytes, countItem: false);
            throw;
        }

        try
        {
            if (afterWriteBeforeCount is not null)
            {
                await afterWriteBeforeCount().ConfigureAwait(false);
            }
        }
        finally
        {
            // Publication is committed; counting + marking is the no-throw path a reader
            // may already be waiting on.
            lock (sync)
            {
                bufferedItems++;
                peakItems = Math.Max(peakItems, bufferedItems);
            }

            entry.MarkCounted();
        }
    }

    public async IAsyncEnumerable<PipelineStreamItem<T>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (Entry entry in channel.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                // Writer publication already committed — no reader token: the count must
                // finish before this entry may be yielded or released.
                await entry.Counted.ConfigureAwait(false);
                yield return entry.Item;
            }
            finally
            {
                // Disposal between dequeue and yield lands here without Counted yet;
                // wait for the writer's mark so the release always pairs an increment.
                if (!entry.Counted.IsCompleted)
                {
                    await entry.Counted.ConfigureAwait(false);
                }

                ReleaseItem(entry.Item.EstimatedBytes, countItem: true);
            }
        }
    }

    public bool TryComplete(Exception? error = null)
    {
        lock (sync)
        {
            if (completed)
            {
                return false;
            }

            completed = true;
            completionError = error;
            Pulse();
        }

        return channel.Writer.TryComplete(error);
    }

    public async ValueTask DisposeAsync()
    {
        TryComplete();

        // Release queue-resident accounting. A writer still between publish and count is
        // awaited via Counted; items already pulled by a reader stay accounted until that
        // enumerator advances/disposes past them.
        while (channel.Reader.TryRead(out Entry? entry))
        {
            await entry.Counted.ConfigureAwait(false);
            ReleaseItem(entry.Item.EstimatedBytes, countItem: true);
        }

        while (true)
        {
            Task wait;
            lock (sync)
            {
                if (bufferedItems == 0 && bufferedBytes == 0)
                {
                    return;
                }

                wait = signal.Task;
            }

            await wait.ConfigureAwait(false);
        }
    }

    private void ReleaseItem(long bytes, bool countItem)
    {
        lock (sync)
        {
            if (bufferedBytes < bytes || (countItem && bufferedItems <= 0))
            {
                throw new InvalidOperationException(
                    "BoundedPipelineChannel accounting underflow: release exceeded "
                        + $"reserved state (bytes {bytes} > {bufferedBytes}, "
                        + $"items {bufferedItems}).");
            }

            bufferedBytes -= bytes;
            if (countItem)
            {
                bufferedItems--;
            }

            Pulse();
        }
    }

    private void Pulse()
    {
        // Re-pulse for every release: waiters re-check capacity under the lock, so a
        // one-shot signal is sufficient and RunContinuationsAsynchronously keeps the
        // continuation off our lock-holding path.
        TaskCompletionSource previous = signal;
        signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private static void Validate(PipelineStreamItem<T> item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Identity);
        if (item.Identity.RunId == Guid.Empty)
        {
            throw new ArgumentException("Stream identity RunId must be non-empty.", nameof(item));
        }

        if (string.IsNullOrWhiteSpace(item.Identity.SnapshotId))
        {
            throw new ArgumentException("Stream identity SnapshotId must be non-blank.", nameof(item));
        }

        if (item.Identity.SegmentIndex < 0)
        {
            throw new ArgumentException("Stream identity SegmentIndex must be non-negative.", nameof(item));
        }

        if (item.Identity.Sequence < 0)
        {
            throw new ArgumentException("Stream identity Sequence must be non-negative.", nameof(item));
        }

        ArgumentNullException.ThrowIfNull(item.Payload);
        if (item.EstimatedBytes <= 0)
        {
            throw new ArgumentException("EstimatedBytes must be positive.", nameof(item));
        }
    }
}
