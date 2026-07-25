using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Pipeline;
using Trackdub.Application.Transcripts.Pipeline;
using Xunit;

namespace Trackdub.Application.Tests.Pipeline;

/// <summary>
/// In-process bus coverage for <see cref="PipelineTransientFaultBus"/> — ring
/// buffer cap, IObservable ordering + lifecycle, project-scoped counts.
/// Mirrors spec §11.3 + §4.3.
/// </summary>
public sealed class PipelineTransientFaultBusTests
{
    [Fact]
    public void Snapshot_is_empty_when_nothing_published()
    {
        using var bus = new PipelineTransientFaultBus();
        Assert.Empty(bus.Snapshot());
    }

    [Fact]
    public void Snapshot_preserves_arrival_order()
    {
        using var bus = new PipelineTransientFaultBus();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PipelineTransientFault first = Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, attempt: 1, happenedAt: now);
        PipelineTransientFault second = Make(first.ProjectId, "Asr", TransientFailureKind.DirectoryLock, attempt: 2, happenedAt: now);
        PipelineTransientFault third = Make(first.ProjectId, "Export", TransientFailureKind.UserCancellation, attempt: 3, happenedAt: now);

        bus.Publish(first);
        bus.Publish(second);
        bus.Publish(third);

        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Same(first, snapshot[0]);
        Assert.Same(second, snapshot[1]);
        Assert.Same(third, snapshot[2]);
    }

    [Fact]
    public void Snapshot_caps_at_50_dropping_oldest_first()
    {
        using var bus = new PipelineTransientFaultBus();
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 60; i++)
        {
            bus.Publish(Make(projectId, "Export", TransientFailureKind.SqliteBusy, attempt: i, happenedAt: now));
        }

        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        Assert.Equal(PipelineTransientFaultBus.Capacity, snapshot.Count);
        Assert.Equal(10, snapshot[0].AttemptNumber);
        Assert.Equal(59, snapshot[49].AttemptNumber);
    }

    [Fact]
    public void Subscribe_receives_historical_then_live_in_arrival_order()
    {
        using var bus = new PipelineTransientFaultBus();
        var collector = new RecordingObserver();

        bus.Publish(Make(Guid.NewGuid(), "Vad", TransientFailureKind.DirectoryLock, attempt: 1, happenedAt: DateTimeOffset.UtcNow));
        bus.Publish(Make(Guid.NewGuid(), "Asr", TransientFailureKind.SqliteBusy, attempt: 2, happenedAt: DateTimeOffset.UtcNow));

        using IDisposable subscription = bus.Subscribe(collector);
        Assert.Equal(2, collector.Items.Count);
        Assert.Equal("Vad", collector.Items[0].StageName);

        bus.Publish(Make(Guid.NewGuid(), "Export", TransientFailureKind.UserCancellation, attempt: 3, happenedAt: DateTimeOffset.UtcNow));
        Assert.Equal(3, collector.Items.Count);
        Assert.Equal("Export", collector.Items[2].StageName);
    }

    [Fact]
    public void Unsubscriber_stops_live_events_to_that_observer()
    {
        using var bus = new PipelineTransientFaultBus();
        var first = new RecordingObserver();
        var second = new RecordingObserver();

        IDisposable firstSubscription = bus.Subscribe(first);
        IDisposable secondSubscription = bus.Subscribe(second);

        bus.Publish(Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, attempt: 1, happenedAt: DateTimeOffset.UtcNow));
        Assert.Single(first.Items);
        Assert.Single(second.Items);

        firstSubscription.Dispose();

        bus.Publish(Make(Guid.NewGuid(), "Export", TransientFailureKind.DirectoryLock, attempt: 2, happenedAt: DateTimeOffset.UtcNow));
        Assert.Single(first.Items);
        Assert.Equal(2, second.Items.Count);

        secondSubscription.Dispose();
    }

    [Fact]
    public void Dispose_completes_observers_and_stops_publishing_non_user_cancellation_kinds()
    {
        var bus = new PipelineTransientFaultBus();
        var observer = new RecordingObserver();
        bus.Subscribe(observer);

        bus.Publish(Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, attempt: 1, happenedAt: DateTimeOffset.UtcNow));
        Assert.Single(observer.Items);
        Assert.Single(bus.Snapshot());

        bus.Dispose();
        Assert.True(observer.Completed);

        bus.Publish(Make(Guid.NewGuid(), "Export", TransientFailureKind.SqliteBusy, attempt: 2, happenedAt: DateTimeOffset.UtcNow));
        Assert.Single(observer.Items); // No additional event to observer post-dispose.
        Assert.Single(bus.Snapshot()); // Non-cancellation kind rejected by Publish gate post-dispose.

        bus.Publish(Make(Guid.NewGuid(), "Export", TransientFailureKind.UserCancellation, attempt: 3, happenedAt: DateTimeOffset.UtcNow));
        Assert.Single(observer.Items); // Observers list drained on Dispose; no further notifications.
        Assert.Equal(2, bus.Snapshot().Count); // UserCancellation still recorded post-dispose.
        Assert.Equal(TransientFailureKind.UserCancellation, bus.Snapshot()[1].Kind);

        bus.Dispose(); // Idempotent.
    }

    [Fact]
    public void Publish_null_throws_argument_null_exception()
    {
        using var bus = new PipelineTransientFaultBus();
        Assert.Throws<ArgumentNullException>(() => bus.Publish(null!));
    }

    [Fact]
    public void CountsByKindForProject_filters_and_aggregates_per_kind()
    {
        using var bus = new PipelineTransientFaultBus();
        Guid keep = Guid.NewGuid();
        Guid drop = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        bus.Publish(Make(keep, "Vad", TransientFailureKind.SqliteBusy, attempt: 1, happenedAt: now));
        bus.Publish(Make(keep, "Vad", TransientFailureKind.SqliteBusy, attempt: 2, happenedAt: now));
        bus.Publish(Make(keep, "Export", TransientFailureKind.DirectoryLock, attempt: 3, happenedAt: now));
        bus.Publish(Make(drop, "Vad", TransientFailureKind.SqliteBusy, attempt: 4, happenedAt: now));

        IReadOnlyDictionary<TransientFailureKind, int> counts = bus.CountsByKindForProject(keep);
        Assert.Equal(2, counts[TransientFailureKind.SqliteBusy]);
        Assert.Equal(1, counts[TransientFailureKind.DirectoryLock]);
        Assert.False(counts.ContainsKey(TransientFailureKind.UserCancellation));
    }

    [Fact]
    public void CountsByKindForProject_rejects_empty_project_id()
    {
        using var bus = new PipelineTransientFaultBus();
        Assert.Throws<ArgumentOutOfRangeException>(() => bus.CountsByKindForProject(Guid.Empty));
    }

    [Fact]
    public void Publish_concurrent_publishers_preserves_arrival_order_per_observer()
    {
        using var bus = new PipelineTransientFaultBus();
        var collector = new RecordingObserver();
        bus.Subscribe(collector);

        const int count = 50;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount * 2,
        };

        Parallel.For(0, count, parallelOptions, i =>
        {
            bus.Publish(Make(Guid.NewGuid(), $"Stage{i}", TransientFailureKind.SqliteBusy, attempt: i, happenedAt: DateTimeOffset.UtcNow));
        });

        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        Assert.Equal(count, snapshot.Count);
        Assert.Equal(count, collector.Items.Count);

        for (int i = 0; i < count; i++)
        {
            Assert.Same(snapshot[i], collector.Items[i]);
        }
    }

    [Fact]
    public void Subscribe_after_dispose_does_not_replay_history_or_throw()
    {
        // Regression for the Subscribe/Dispose race the per-observer delivery-gate rework
        // introduced: Subscribe now short-circuits with `if (disposed) return new
        // Unsubscriber(this, entry);` *before* registering the entry or replaying ring history.
        // A late subscriber against an already-disposed bus must get a harmless no-op
        // subscription (no historical replay, no crash) rather than observing stale faults or
        // throwing while racing the drained `observers` list.
        var bus = new PipelineTransientFaultBus();
        bus.Publish(Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, attempt: 1, happenedAt: DateTimeOffset.UtcNow));
        bus.Dispose();

        var collector = new RecordingObserver();
        IDisposable subscription = bus.Subscribe(collector);

        Assert.Empty(collector.Items);
        Assert.False(collector.Completed);

        // Disposing the returned handle must be a harmless no-op (including double-dispose),
        // not throw despite the entry never having been added to the bus's observer list.
        subscription.Dispose();
        subscription.Dispose();
    }

    [Fact]
    public void Unsubscribe_prevents_subsequent_delivery()
    {
        using var bus = new PipelineTransientFaultBus();
        var collector = new RecordingObserver();
        IDisposable subscription = bus.Subscribe(collector);

        bus.Publish(Make(Guid.NewGuid(), "Vad", TransientFailureKind.SqliteBusy, attempt: 1, happenedAt: DateTimeOffset.UtcNow));
        Assert.Single(collector.Items);

        subscription.Dispose();

        bus.Publish(Make(Guid.NewGuid(), "Asr", TransientFailureKind.DirectoryLock, attempt: 2, happenedAt: DateTimeOffset.UtcNow));
        Assert.Single(collector.Items);
    }

    private static PipelineTransientFault Make(
        Guid projectId,
        string stageName,
        TransientFailureKind kind,
        int attempt,
        DateTimeOffset happenedAt) =>
        new(projectId, stageName, kind, $"detail {attempt}", happenedAt, attempt);

    private sealed class RecordingObserver : IObserver<PipelineTransientFault>
    {
        public List<PipelineTransientFault> Items { get; } = new();
        public bool Completed { get; private set; }

        public void OnCompleted() => Completed = true;
        public void OnError(Exception error) => Completed = true;
        public void OnNext(PipelineTransientFault value) => Items.Add(value);
    }
}
