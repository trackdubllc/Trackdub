using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Stamps every progress event of one engine run with that run's identity and a strictly
/// increasing sequence number in delivery order, so downstream consumers can attribute
/// and order events without relying on wall-clock time. The wrapper owns identity: any
/// RunId or SequenceNumber carried by an incoming event is overwritten. With no inner
/// reporter this is a no-op.
/// </summary>
internal sealed class RunScopedProgressReporter(
    Guid runId,
    IProgress<PipelineProgressEvent>? inner) : IProgress<PipelineProgressEvent>
{
    private readonly object gate = new();
    private long sequence;

    public void Report(PipelineProgressEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (inner is null)
        {
            return;
        }

        lock (gate)
        {
            inner.Report(value with
            {
                RunId = runId,
                SequenceNumber = ++sequence,
            });
        }
    }
}
