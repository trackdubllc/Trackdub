using System.Collections.ObjectModel;
using System.Linq;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Pipeline;

namespace Trackdub.Application.Transcripts.Pipeline;

/// <summary>
/// In-process pub/sub for transient-fault records shipped during a single
/// dubbing pipeline run. Bounded ring buffer (last <see cref="Capacity"/>) so a
/// retry storm cannot OOM the host. Each subscription yields live events plus
/// any past snapshot in arrival order. See
/// <c>docs/internal/pipeline-readiness-spec.md</c> section 4.3 + 11.3.
/// </summary>
public sealed class PipelineTransientFaultBus : IObservable<PipelineTransientFault>, IDisposable
{
    /// <summary>Maximum number of faults retained in the snapshot ring buffer.</summary>
    public const int Capacity = 50;

    private readonly object gate = new();
    private readonly LinkedList<RingEntry> ring = new();
    private readonly List<ObserverEntry> observers = new();
    private long nextSequence = 1;
    private bool disposed;

    /// <summary>
    /// Adds <paramref name="fault"/> to the snapshot ring and notifies every
    /// active subscriber. <see cref="TransientFailureKind.UserCancellation"/>
    /// is the only kind that publishes even after disposal, so the user-action
    /// event is never silenced (spec section 4.3).
    /// </summary>
    public void Publish(PipelineTransientFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        long sequence;
        ObserverEntry[] snapshot;
        lock (gate)
        {
            if (disposed && fault.Kind != TransientFailureKind.UserCancellation)
            {
                return;
            }

            sequence = nextSequence++;
            ring.AddLast(new RingEntry(fault, sequence));
            while (ring.Count > Capacity)
            {
                ring.RemoveFirst();
            }

            snapshot = observers.ToArray();
        }

        foreach (ObserverEntry entry in snapshot)
        {
            DeliverToObserver(entry, fault, sequence);
        }
    }

    /// <summary>
    /// Returns the current snapshot in arrival order (oldest first).
    /// </summary>
    public IReadOnlyList<PipelineTransientFault> Snapshot()
    {
        lock (gate)
        {
            if (ring.Count == 0)
            {
                return Array.Empty<PipelineTransientFault>();
            }

            return new ReadOnlyCollection<PipelineTransientFault>(ring.Select(static r => r.Fault).ToArray());
        }
    }

    /// <summary>
    /// Per-stage aggregate over the current snapshot for the supplied project.
    /// Used by the diagnostics-bundle transient section.
    /// </summary>
    public IReadOnlyDictionary<TransientFailureKind, int> CountsByKindForProject(Guid projectId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(projectId, Guid.Empty);

        var counts = new Dictionary<TransientFailureKind, int>();
        lock (gate)
        {
            foreach (RingEntry entry in ring)
            {
                if (entry.Fault.ProjectId != projectId)
                {
                    continue;
                }

                counts.TryGetValue(entry.Fault.Kind, out int existing);
                counts[entry.Fault.Kind] = existing + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Per-run, per-stage aggregation snapshot for the supplied project —
    /// <see cref="PipelineTransientFaultBus.Snapshot"/> filtered to one
    /// <paramref name="projectId"/> and grouped by stage-then-kind in arrival
    /// order. Honours the §9.1 (b) per-run aggregation recommendation;
    /// idempotent across repeated calls (the underlying ring buffer is the
    /// source of truth, not caller-side state). See
    /// <c>docs/internal/pipeline-readiness-spec.md</c> §9.1 + §11.4.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="projectId"/> is <see cref="Guid.Empty"/>.</exception>
    public PipelineTransientFaultRunSnapshot SnapshotPerRun(Guid projectId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(projectId, Guid.Empty);

        List<PipelineTransientFault> filtered;
        lock (gate)
        {
            filtered = ring
                .Where(entry => entry.Fault.ProjectId == projectId)
                .Select(entry => entry.Fault)
                .ToList();
        }

        var stages = new List<string>();
        var counts = new Dictionary<string, Dictionary<TransientFailureKind, int>>(StringComparer.Ordinal);
        foreach (PipelineTransientFault fault in filtered)
        {
            if (!counts.TryGetValue(fault.StageName, out Dictionary<TransientFailureKind, int>? stageMap))
            {
                stageMap = new Dictionary<TransientFailureKind, int>();
                counts[fault.StageName] = stageMap;
                stages.Add(fault.StageName);
            }

            stageMap.TryGetValue(fault.Kind, out int existing);
            stageMap[fault.Kind] = existing + 1;
        }


        var immutableCounts = new Dictionary<string, IReadOnlyDictionary<TransientFailureKind, int>>(counts.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, Dictionary<TransientFailureKind, int>> kv in counts)
        {
            immutableCounts[kv.Key] = new ReadOnlyDictionary<TransientFailureKind, int>(kv.Value);
        }


        return new PipelineTransientFaultRunSnapshot(
            ProjectId: projectId,
            Faults: new System.Collections.ObjectModel.ReadOnlyCollection<PipelineTransientFault>(filtered),
            StagesInArrivalOrder: new System.Collections.ObjectModel.ReadOnlyCollection<string>(stages),
            CountsByStageAndKind: new ReadOnlyDictionary<string, IReadOnlyDictionary<TransientFailureKind, int>>(immutableCounts));
    }

    /// <inheritdoc />
    public IDisposable Subscribe(IObserver<PipelineTransientFault> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        var entry = new ObserverEntry(observer);
        RingEntry[] historical;
        long nextSeq;

        // Acquire the per-observer delivery gate before exposing the entry to Publish
        // so a concurrent Publish cannot deliver a live fault before the historical
        // replay has completed.
        lock (entry.DeliveryGate)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return new Unsubscriber(this, entry);
                }

                observers.Add(entry);
                historical = ring.ToArray();
                nextSeq = nextSequence;

                // Mark active while still holding both locks so a concurrent Publish
                // snapshot never sees an inactive-but-registered observer, and set
                // NextSequence from the consistent historical snapshot before replay.
                entry.IsActive = true;
                entry.NextSequence = historical.Length > 0 ? historical[^1].Sequence + 1 : nextSeq;
            }

            foreach (RingEntry ringEntry in historical)
            {
                OnNextWithExceptionHandling(entry, ringEntry.Fault);
            }
        }

        return new Unsubscriber(this, entry);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ObserverEntry[] drained;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            drained = observers.ToArray();
            observers.Clear();
        }

        foreach (ObserverEntry entry in drained)
        {
            lock (entry.DeliveryGate)
            {
                entry.IsActive = false;
                try
                {
                    entry.Observer.OnCompleted();
                }
                catch (Exception)
                {
                    // Per .NET observer contract; mirror Publish's fault-tolerance.
                }
            }
        }
    }

    private void Unsubscribe(ObserverEntry entry)
    {
        lock (entry.DeliveryGate)
        {
            entry.IsActive = false;
        }

        lock (gate)
        {
            observers.Remove(entry);
        }
    }

    private void DeliverToObserver(ObserverEntry entry, PipelineTransientFault fault, long sequence)
    {
        lock (entry.DeliveryGate)
        {
            if (!entry.IsActive || sequence < entry.NextSequence)
            {
                return;
            }

            if (sequence > entry.NextSequence)
            {
                entry.Pending[sequence] = fault;
                return;
            }

            OnNextWithExceptionHandling(entry, fault);
            entry.NextSequence++;

            while (entry.Pending.TryGetValue(entry.NextSequence, out PipelineTransientFault? pending))
            {
                entry.Pending.Remove(entry.NextSequence);
                if (pending is not null)
                {
                    OnNextWithExceptionHandling(entry, pending);
                }

                entry.NextSequence++;
            }
        }
    }

    private static void OnNextWithExceptionHandling(ObserverEntry entry, PipelineTransientFault fault)
    {
        try
        {
            entry.Observer.OnNext(fault);
        }
        catch (Exception)
        {
            // Subscriber exceptions are local to that subscriber; per .NET observer
            // contract a misbehaving observer cannot block sibling observers or the
            // publisher. The bus does not log here; pipeline log path owns the trace.
        }
    }

    private readonly record struct RingEntry(PipelineTransientFault Fault, long Sequence);

    private sealed class ObserverEntry
    {
        public ObserverEntry(IObserver<PipelineTransientFault> observer)
        {
            Observer = observer;
            DeliveryGate = new object();
            Pending = new Dictionary<long, PipelineTransientFault>();
        }

        public IObserver<PipelineTransientFault> Observer { get; }
        public object DeliveryGate { get; }
        public bool IsActive { get; set; }
        public long NextSequence { get; set; }
        public Dictionary<long, PipelineTransientFault> Pending { get; }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly PipelineTransientFaultBus owner;
        private readonly ObserverEntry entry;
        private bool disposed;

        public Unsubscriber(PipelineTransientFaultBus owner, ObserverEntry entry)
        {
            this.owner = owner;
            this.entry = entry;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.Unsubscribe(entry);
        }
    }
}
