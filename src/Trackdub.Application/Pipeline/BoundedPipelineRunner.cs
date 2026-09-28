using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Runs one producer and one consumer joined through a <see cref="BoundedPipelineChannel{T}"/>.
/// Either worker finishing — success, fault, or cancellation — ends the pair: the channel is
/// completed (faulted with non-cancellation errors), the linked token cancels the survivor,
/// and both workers are fully observed before the channel is disposed. No worker survives
/// a fault, cancellation, or early consumer return. A non-cancellation fault wins over both
/// worker and caller cancellation — the real error is more diagnosable than the shutdown
/// it triggered.
/// </summary>
public static class BoundedPipelineRunner
{
    public static async Task RunAsync<T>(
        BoundedPipelineChannelOptions options,
        Func<BoundedPipelineChannel<T>, CancellationToken, Task> producer,
        Func<IAsyncEnumerable<PipelineStreamItem<T>>, CancellationToken, Task> consumer,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(consumer);

        await using var channel = new BoundedPipelineChannel<T>(options);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Exception? failure = null;
        OperationCanceledException? workerCancellation = null;
        bool callerCancelled = false;
        bool weCancelledLinked = false;

        async Task ObserveAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                callerCancelled = true;
            }
            catch (OperationCanceledException ex)
            {
                // An OCE from a worker we canceled via the linked token is the expected
                // shutdown path, not a fault.
                if (!weCancelledLinked)
                {
                    workerCancellation ??= ex;
                }
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        async Task ProducerWorkerAsync()
        {
            try
            {
                await producer(channel, linkedCts.Token).ConfigureAwait(false);
                channel.TryComplete();
            }
            catch (OperationCanceledException)
            {
                // Cancellation completes the channel normally — a reader waiting on a
                // canceled producer observes end-of-stream, not a fault.
                channel.TryComplete();
                throw;
            }
            catch (Exception ex)
            {
                channel.TryComplete(ex);
                throw;
            }
        }

        // Wrapper task so a synchronous delegate throw is captured, not escaped inline.
        async Task ConsumerWorkerAsync()
        {
            await consumer(channel.ReadAllAsync(linkedCts.Token), linkedCts.Token).ConfigureAwait(false);
        }

        Task producerTask = ProducerWorkerAsync();
        Task consumerTask = ConsumerWorkerAsync();

        // WhenAny first: a faulting/returning consumer must be able to cancel a producer
        // blocked on a full channel — WhenAll could deadlock on that pair.
        Task first = await Task.WhenAny(producerTask, consumerTask).ConfigureAwait(false);
        await ObserveAsync(first).ConfigureAwait(false);

        Task remaining = ReferenceEquals(first, producerTask) ? consumerTask : producerTask;
        if (!remaining.IsCompleted
            && (ReferenceEquals(first, consumerTask) // consumer finished/faulted early
                || failure is not null
                || callerCancelled
                || workerCancellation is not null))
        {
            weCancelledLinked = true;
            try
            {
                await linkedCts.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A throwing cancellation callback must not strand the surviving worker.
                failure ??= ex;
            }
        }

        // Always observe the second worker — completed or not — so no task goes unobserved.
        await ObserveAsync(remaining).ConfigureAwait(false);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (workerCancellation is not null)
        {
            ExceptionDispatchInfo.Capture(workerCancellation).Throw();
        }
    }
}
