using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Pipeline;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;
using Xunit;

namespace Trackdub.Application.Tests;

public sealed class StageRunHelperTests
{
    private sealed class Reporter : IStageRuntimeExecutionReporter
    {
        public StageRuntimeExecutionSummary? LastExecutionSummary { get; set; }
    }

    [Fact]
    public async Task ApplyRuntimeExecutionSummaryAsync_PersistsBenchmarkEvidenceId()
    {
        var stageRun = StageRunRecord.Start(Guid.NewGuid(), StageNames.Asr, DateTimeOffset.UtcNow);
        var reporter = new Reporter
        {
            LastExecutionSummary = new StageRuntimeExecutionSummary(
                "cpu",
                "cpu",
                "model",
                "alias",
                "default",
                null)
        };
        IRuntimePlanningPreferences preferences = new FakeRuntimePlanningPreferences
        {
            BenchmarkEvidenceId = "bench-evidence-42"
        };

        StageRunRecord updated = await StageRunHelper.ApplyRuntimeExecutionSummaryAsync(
            stageRun,
            reporter,
            preferences,
            CancellationToken.None);

        Assert.Equal("bench-evidence-42", updated.RuntimeInfo?.BenchmarkEvidenceId);
    }

    [Fact]
    public async Task RunStageAsync_CompletesStageRunAndReturnsResult()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();

        (StageRunRecord stageRun, string result) = await StageRunHelper.RunStageAsync(
            store,
            projectId,
            StageNames.Asr,
            runtimeReporter: null,
            static (_, _) => Task.FromResult("done"),
            canceledReason: "ASR canceled.",
            CancellationToken.None);

        Assert.Equal("done", result);
        Assert.Equal(StageRunStatus.Completed, stageRun.Status);
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(stageRun.Id, stored.Id);
        Assert.Equal(StageRunStatus.Completed, stored.Status);
    }

    [Fact]
    public async Task RunStageAsync_FailsStageRunBeforeRethrowing()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StageRunHelper.RunStageAsync<string>(
                store,
                projectId,
                StageNames.Asr,
                runtimeReporter: null,
                static (_, _) => throw new InvalidOperationException("boom"),
                canceledReason: "ASR canceled.",
                CancellationToken.None));

        Assert.Equal("boom", exception.Message);
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Failed, stored.Status);
        Assert.Equal("boom", stored.FailureReason);
    }

    [Fact]
    public async Task RunStageAsync_MarksCanceled_WhenOperationCanceledIsThrown()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            StageRunHelper.RunStageAsync<string>(
                store,
                projectId,
                StageNames.Asr,
                runtimeReporter: null,
                static (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult("unreached");
                },
                canceledReason: "ASR canceled.",
                cts.Token));

        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Canceled, stored.Status);
        Assert.Equal("ASR canceled.", stored.FailureReason);
    }

    [Fact]
    public async Task SkipAsync_MarksStageRunAsSkipped_AndPersistsReason()
    {
        var store = new FakeProjectStageRunStore();
        StageRunRecord stageRun = await StageRunHelper.StartAsync(
            store,
            Guid.NewGuid(),
            StageNames.Asr,
            CancellationToken.None);

        StageRunRecord skipped = await StageRunHelper.SkipAsync(
            store,
            stageRun,
            runtimeReporter: null,
            "VAD_NO_REGIONS",
            CancellationToken.None);

        Assert.Equal(StageRunStatus.Skipped, skipped.Status);
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Skipped, stored.Status);
        Assert.Equal("VAD_NO_REGIONS", stored.FailureReason);
    }

    [Fact]
    public async Task PartiallyCompleteAsync_PersistsPartialState_WhenSomeWorkSucceeded()
    {
        var store = new FakeProjectStageRunStore();
        StageRunRecord stageRun = await StageRunHelper.StartAsync(
            store,
            Guid.NewGuid(),
            StageNames.Tts,
            CancellationToken.None);

        StageRunRecord partial = await StageRunHelper.PartiallyCompleteAsync(
            store,
            stageRun,
            runtimeReporter: null,
            "TTS generated 1 take(s) before failing: boom",
            CancellationToken.None);

        Assert.Equal(StageRunStatus.PartiallyCompleted, partial.Status);
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.PartiallyCompleted, stored.Status);
    }

    [Fact]
    public async Task FailAsync_FallsBackToTimeoutWriter_WhenTokenAlreadyCanceled()
    {
        var store = new FakeProjectStageRunStore();
        StageRunRecord stageRun = await StageRunHelper.StartAsync(
            store,
            Guid.NewGuid(),
            StageNames.Asr,
            CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        StageRunRecord failed = await StageRunHelper.FailAsync(
            store,
            stageRun,
            runtimeReporter: null,
            "boom",
            cts.Token);

        Assert.Equal(StageRunStatus.Failed, failed.Status);
        // Persistence must succeed via the 5s fallback timeout path even though the
        // primary token was already canceled at the helper's call.
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Failed, stored.Status);
    }

    [Fact]
    public async Task RunStageAsync_PublishesToBus_WhenTransientFailureOccurs()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        var bus = new PipelineTransientFaultBus();

        // Dual-marker fixture: POSIX substring EAGAIN matches the classifier's POSIX branch on
        // non-Windows hosts; ERROR_SHARING_VIOLATION hresult 0x80070020 matches the Windows-host
        // branch. Either path resolves to DirectoryLock so the test is host-portable without an
        // OS-marker switch. See TransientFailureKind.cs ClassifyIOException for the runtime-OS
        // dispatch — keep the dual-marker pattern for any new Application.Tests IOException fixture.
        IOException ex = new("foo [EAGAIN] bar", unchecked((int)0x80070020));

        await Assert.ThrowsAsync<IOException>(() =>
            StageRunHelper.RunStageAsync<string>(
                store,
                projectId,
                StageNames.Asr,
                runtimeReporter: null,
                (_, _) => throw ex,
                canceledReason: "ASR canceled.",
                CancellationToken.None,
                runtimePlanningPreferences: null,
                logger: null,
                transientFaultBus: bus));

        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(TransientFailureKind.DirectoryLock, snapshot[0].Kind);
        Assert.Equal(StageNames.Asr, snapshot[0].StageName);
        Assert.Equal(projectId, snapshot[0].ProjectId);
        // Spec §4.4: publish to the bus, then mark the row Failed. RunStageAsync has no retry
        // loop, so the stage run must be terminal before rethrowing.
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Failed, stored.Status);
        Assert.Equal(ex.Message, stored.FailureReason);
    }

    [Fact]
    public async Task RunStageAsync_TransientFailure_PersistsFailedRow_AndRethrowsOriginalException_WhenCancellationFiresConcurrently()
    {
        // Regression for the concurrent-cancellation gap flagged in PR review: RunStageAsync's
        // transient catch arm calls FailAsync with the caller's own cancellationToken. If that
        // token is (or becomes) canceled while a transient exception is in flight, FailAsync
        // must still terminalize the row via PersistTerminalAsync's canceled-token fallback
        // path, and the ORIGINAL transient exception — not an OperationCanceledException from
        // the failed persistence attempt — must be what RunStageAsync rethrows.
        var store = new FakeCancellationAwareProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        var bus = new PipelineTransientFaultBus();
        using var cts = new CancellationTokenSource();

        // Dual-marker fixture: see other transient-fixture comments in this file.
        IOException transientEx = new("foo [EAGAIN] bar", unchecked((int)0x80070020));

        IOException result = await Assert.ThrowsAsync<IOException>(() =>
            StageRunHelper.RunStageAsync<string>(
                store,
                projectId,
                StageNames.Asr,
                runtimeReporter: null,
                (_, _) =>
                {
                    cts.Cancel();
                    throw transientEx;
                },
                canceledReason: "ASR canceled.",
                cts.Token,
                runtimePlanningPreferences: null,
                logger: null,
                transientFaultBus: bus));

        Assert.Same(transientEx, result);
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Failed, stored.Status);
        Assert.Equal(transientEx.Message, stored.FailureReason);

        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        PipelineTransientFault published = Assert.Single(snapshot);
        Assert.Equal(TransientFailureKind.DirectoryLock, published.Kind);
        Assert.Equal(StageNames.Asr, published.StageName);
        Assert.Equal(projectId, published.ProjectId);
    }

    [Fact]
    public async Task RunStageAsync_DoesNotPublishToBus_WhenNonTransientFailureOccurs()
    {
        var store = new FakeProjectStageRunStore();
        var bus = new PipelineTransientFaultBus();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            StageRunHelper.RunStageAsync<string>(
                store,
                Guid.NewGuid(),
                StageNames.Asr,
                runtimeReporter: null,
                static (_, _) => throw new ArgumentException("not transient"),
                canceledReason: "ASR canceled.",
                CancellationToken.None,
                runtimePlanningPreferences: null,
                logger: null,
                transientFaultBus: bus));

        Assert.Empty(bus.Snapshot());
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Failed, stored.Status);
    }

    [Fact]
    public async Task RunStageAsync_PublishesUserCancellation_AndRecordsCancelRow()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        var bus = new PipelineTransientFaultBus();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            StageRunHelper.RunStageAsync<string>(
                store,
                projectId,
                StageNames.Asr,
                runtimeReporter: null,
                static (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult("unreached");
                },
                canceledReason: "ASR canceled.",
                cts.Token,
                runtimePlanningPreferences: null,
                logger: null,
                transientFaultBus: bus));

        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(TransientFailureKind.UserCancellation, snapshot[0].Kind);
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Canceled, stored.Status);
        Assert.Equal("ASR canceled.", stored.FailureReason);
    }

    [Fact]
    public async Task RunStageWithTransientRetry_succeeds_on_third_attempt_publishes_two_faults()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        var bus = new PipelineTransientFaultBus();
        int attempts = 0;

        // Dual-marker fixture: POSIX substring EAGAIN matches the classifier's POSIX branch on
        // non-Windows hosts; ERROR_SHARING_VIOLATION hresult 0x80070020 matches the Windows-host
        // branch. Host-portable; see TransientFailureKind.cs.
        IOException transientEx = new("foo [EAGAIN] bar", unchecked((int)0x80070020));

        (StageRunRecord stageRun, string result) = await StageRunHelper.RunStageWithTransientRetryAsync<string>(
            store,
            bus,
            projectId,
            StageNames.Asr,
            runtimeReporter: null,
            (_, _) =>
            {
                attempts++;
                // Fail the first two attempts so the bus records AttemptNumber 1 and 2, then succeed.
                if (attempts < 3)
                {
                    throw transientEx;
                }

                return Task.FromResult("done");
            },
            canceledReason: "ASR canceled.",
            CancellationToken.None,
            retryBudget: new StageRetryBudget(maxAttempts: 3, baseBackoffMs: 1));

        // Three attempts: two transient faults then success. Bus carries two fault records.
        Assert.Equal(3, attempts);
        Assert.Equal("done", result);
        Assert.Equal(StageRunStatus.Completed, stageRun.Status);
        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(1, snapshot[0].AttemptNumber);
        Assert.Equal(2, snapshot[1].AttemptNumber);
        Assert.Equal(TransientFailureKind.DirectoryLock, snapshot[0].Kind);
        Assert.Equal(TransientFailureKind.DirectoryLock, snapshot[1].Kind);
        // Both faults reference the same StageRunRecord id because the retry helper reuses one row.
        Assert.Equal(snapshot[0].Context!["StageRunId"], snapshot[1].Context!["StageRunId"]);
        // Single row landed in the store with terminal Completed status.
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Completed, stored.Status);
    }

    [Fact]
    public async Task RunStageWithTransientRetry_exhausts_three_attempts_rethrows()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        var bus = new PipelineTransientFaultBus();
        int attempts = 0;
        // Dual-marker fixture: host-portable IOException triggers DirectoryLock on either host
        // branch without runtime-OS gating the test body. See TransientFailureKind.cs.
        IOException transientEx = new("foo [EAGAIN] bar", unchecked((int)0x80070020));

        IOException result = await Assert.ThrowsAsync<IOException>(() =>
            StageRunHelper.RunStageWithTransientRetryAsync<string>(
                store,
                bus,
                projectId,
                StageNames.Asr,
                runtimeReporter: null,
                (_, _) =>
                {
                    attempts++;
                    throw transientEx;
                },
                canceledReason: "ASR canceled.",
                CancellationToken.None,
                retryBudget: new StageRetryBudget(maxAttempts: 3, baseBackoffMs: 1)));

        Assert.Equal(3, attempts);
        Assert.Same(transientEx, result);
        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(TransientFailureKind.DirectoryLock, snapshot[2].Kind);
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Failed, stored.Status);
        Assert.NotNull(stored.FailureReason);
        Assert.Contains("transient retry exhausted", stored.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunStageWithTransientRetry_UserCancellation_writes_Canceled_row_before_rethrow()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        var bus = new PipelineTransientFaultBus();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            // Cancel mid-first-attempt: helper must mark row Canceled, publish UserCancellation, and rethrow.
            async Task<string> Runner(StageRunRecord _, CancellationToken ct)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return "unreached";
            }
            await StageRunHelper.RunStageWithTransientRetryAsync<string>(
                store,
                bus,
                projectId,
                StageNames.Asr,
                runtimeReporter: null,
                Runner,
                canceledReason: "ASR canceled.",
                cts.Token,
                retryBudget: new StageRetryBudget(maxAttempts: 3, baseBackoffMs: 1));
        });

        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Canceled, stored.Status);
        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(TransientFailureKind.UserCancellation, snapshot[0].Kind);
    }

    [Fact]
    public async Task RunStageWithTransientRetry_bubbles_non_transient_exception_unchanged()
    {
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        var bus = new PipelineTransientFaultBus();
        int attempts = 0;

        ArgumentException result = await Assert.ThrowsAsync<ArgumentException>(() =>
            StageRunHelper.RunStageWithTransientRetryAsync<string>(
                store,
                bus,
                projectId,
                StageNames.Asr,
                runtimeReporter: null,
                (_, _) =>
                {
                    attempts++;
                    throw new ArgumentException("not transient");
                },
                canceledReason: "ASR canceled.",
                CancellationToken.None,
                retryBudget: new StageRetryBudget(maxAttempts: 3, baseBackoffMs: 1)));

        Assert.Equal(1, attempts); // No retry on non-transient
        Assert.Equal("not transient", result.Message);
        Assert.Empty(bus.Snapshot()); // No bus publish on non-transient
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Failed, stored.Status);
    }

    [Fact]
    public async Task RunStageWithTransientRetry_aborts_after_one_attempt_when_budget_max_is_one()
    {
        // Spec §11.9: the per-stage budget is injected; the helper must honor it even when it
        // shrinks MaxAttempts below the legacy default. Without injection the helper would have
        // attempted twice and then succeeded; with a budget of 1 the second throw rethrows and the
        // row lands in SQLite as Failed with the transient-retry-exhausted message.
        var store = new FakeProjectStageRunStore();
        Guid projectId = Guid.NewGuid();
        var bus = new PipelineTransientFaultBus();
        int attempts = 0;
        IOException transientEx = new("foo [EAGAIN] bar", unchecked((int)0x80070020));

        IOException result = await Assert.ThrowsAsync<IOException>(() =>
            StageRunHelper.RunStageWithTransientRetryAsync<string>(
                store,
                bus,
                projectId,
                StageNames.Asr,
                runtimeReporter: null,
                (_, _) =>
                {
                    attempts++;
                    throw transientEx;
                },
                canceledReason: "ASR canceled.",
                CancellationToken.None,
                retryBudget: new StageRetryBudget(maxAttempts: 1, baseBackoffMs: 1)));

        Assert.Equal(1, attempts);
        Assert.Same(transientEx, result);
        IReadOnlyList<PipelineTransientFault> snapshot = bus.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(TransientFailureKind.DirectoryLock, snapshot[0].Kind);
        StageRunRecord stored = Assert.Single(store.All);
        Assert.Equal(StageRunStatus.Failed, stored.Status);
        Assert.Contains("transient retry exhausted", stored.FailureReason!, StringComparison.Ordinal);
    }
}
