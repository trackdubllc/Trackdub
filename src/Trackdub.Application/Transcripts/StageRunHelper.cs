using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Pipeline;
using Trackdub.Domain.StageRuns;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Application.Transcripts;

internal static class StageRunHelper
{
    public static async Task<StageRunRecord> StartAsync(
        IProjectStageRunStore stageRunStore,
        Guid projectId,
        string stageName,
        CancellationToken cancellationToken)
    {
        StageRunRecord stageRun = StartKnownStageRun(projectId, stageName, DateTimeOffset.UtcNow);
        await stageRunStore.CreateAsync(stageRun, cancellationToken).ConfigureAwait(false);
        return stageRun;
    }

    private static StageRunRecord StartKnownStageRun(
        Guid projectId,
        string stageName,
        DateTimeOffset startedAt) =>
        stageName switch
        {
            StageNames.Vad => StageRunRecord.Start(projectId, StageNames.Vad, startedAt),
            StageNames.Asr => StageRunRecord.Start(projectId, StageNames.Asr, startedAt),
            StageNames.Diarization => StageRunRecord.Start(projectId, StageNames.Diarization, startedAt),
            StageNames.SpeakerAssignment => StageRunRecord.Start(projectId, StageNames.SpeakerAssignment, startedAt),
            StageNames.Translation => StageRunRecord.Start(projectId, StageNames.Translation, startedAt),
            StageNames.Tts => StageRunRecord.Start(projectId, StageNames.Tts, startedAt),
            StageNames.Separation => StageRunRecord.Start(projectId, StageNames.Separation, startedAt),
            StageNames.SpeechEnhancement => StageRunRecord.Start(projectId, StageNames.SpeechEnhancement, startedAt),
            StageNames.AudioPreparation => StageRunRecord.Start(projectId, StageNames.AudioPreparation, startedAt),
            StageNames.PreviewMix => StageRunRecord.Start(projectId, StageNames.PreviewMix, startedAt),
            StageNames.VoiceCloning => StageRunRecord.Start(projectId, StageNames.VoiceCloning, startedAt),
            StageNames.Export => StageRunRecord.Start(projectId, StageNames.Export, startedAt),
            StageNames.LipSync => StageRunRecord.Start(projectId, StageNames.LipSync, startedAt),
            StageNames.LipSynthesis => StageRunRecord.Start(projectId, StageNames.LipSynthesis, startedAt),
            StageNames.OverlapRescue => StageRunRecord.Start(projectId, StageNames.OverlapRescue, startedAt),
            StageNames.TextRefinementAsr => StageRunRecord.Start(projectId, StageNames.TextRefinementAsr, startedAt),
            StageNames.TextRefinementTranslation => StageRunRecord.Start(projectId, StageNames.TextRefinementTranslation, startedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(stageName), stageName, "Stage run name must use a known StageNames value.")
        };

    public static async Task<(StageRunRecord StageRun, TResult Result)> RunStageAsync<TResult>(
        IProjectStageRunStore stageRunStore,
        Guid projectId,
        string stageName,
        object? runtimeReporter,
        Func<StageRunRecord, CancellationToken, Task<TResult>> runAsync,
        string canceledReason,
        CancellationToken cancellationToken,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null,
        IApplicationLogger? logger = null,
        PipelineTransientFaultBus? transientFaultBus = null)
    {
        ArgumentNullException.ThrowIfNull(stageRunStore);
        ArgumentNullException.ThrowIfNull(runAsync);

        StageRunRecord stageRun = await StartAsync(stageRunStore, projectId, stageName, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            TResult result = await runAsync(stageRun, cancellationToken).ConfigureAwait(false);
            stageRun = await CompleteAsync(
                    stageRunStore,
                    stageRun,
                    runtimeReporter,
                    cancellationToken,
                    runtimePlanningPreferences)
                .ConfigureAwait(false);
            return (stageRun, result);
        }
        catch (OperationCanceledException)
        {
            // Spec §4.6: capture the cancel row before rethrowing so SQLite does not lie with a stale Running row.
            // Also publish UserCancellation to the bus surface so the operator-visible state matches the persisted row.
            if (transientFaultBus is not null)
            {
                PublishTransient(stageRun, projectId, stageName, TransientFailureKind.UserCancellation,
                    "Stage canceled by caller.", attemptNumber: 1, transientFaultBus);
            }
            await CancelAsync(
                    stageRunStore,
                    stageRun,
                    runtimeReporter,
                    canceledReason,
                    CancellationToken.None,
                    runtimePlanningPreferences,
                    logger)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (TransientFailureClassifier.IsTransient(ex))
        {
            // Spec §4.4: publish to the bus when one is supplied, then mark the row Failed.
            // RunStageAsync has no retry loop, so the stage run must be terminal before rethrowing.
            if (transientFaultBus is not null)
            {
                TransientFailureKind kind = TransientFailureClassifier.Classify(ex);
                PublishTransient(stageRun, projectId, stageName, kind, ex.Message,
                    attemptNumber: 1, transientFaultBus, ex);
            }

            await FailAsync(
                    stageRunStore,
                    stageRun,
                    runtimeReporter,
                    ex.Message,
                    cancellationToken,
                    runtimePlanningPreferences,
                    logger)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(
                    stageRunStore,
                    stageRun,
                    runtimeReporter,
                    ex.Message,
                    cancellationToken,
                    runtimePlanningPreferences,
                    logger)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static void PublishTransient(
        StageRunRecord stageRun,
        Guid projectId,
        string stageName,
        TransientFailureKind kind,
        string detail,
        int attemptNumber,
        PipelineTransientFaultBus bus,
        Exception? ex = null)
    {
        var context = new Dictionary<string, string>
        {
            ["StageRunId"] = stageRun.Id.ToString("N"),
        };
        if (ex is not null && ex.GetType().FullName is { Length: > 0 } typeName)
        {
            context["ExceptionType"] = typeName;
        }
        bus.Publish(new PipelineTransientFault(
            projectId,
            stageName,
            kind,
            detail,
            DateTimeOffset.UtcNow,
            attemptNumber,
            context));
    }

    /// <summary>
    /// Bounded retry helper that wraps <see cref="RunStageAsync{TResult}"/> semantically — but
    /// owns its own <see cref="StageRunRecord"/> lifecycle so retry attempts don't accumulate
    /// stale Running rows in SQLite. On exhaustion the row is marked Failed via
    /// <see cref="FailAsync"/>. Transient faults are re-thrown to the bus surface on each
    /// attempt. Cancellation is not retried. See
    /// <c>docs/internal/pipeline-readiness-spec.md</c> section 4.4 + 11.2 + 11.9.
    /// </summary>
    public static async Task<(StageRunRecord StageRun, TResult Result)> RunStageWithTransientRetryAsync<TResult>(
        IProjectStageRunStore stageRunStore,
        PipelineTransientFaultBus transientFaultBus,
        Guid projectId,
        string stageName,
        object? runtimeReporter,
        Func<StageRunRecord, CancellationToken, Task<TResult>> runAsync,
        string canceledReason,
        CancellationToken cancellationToken,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null,
        IApplicationLogger? logger = null,
        StageRetryBudget? retryBudget = null)
    {
        ArgumentNullException.ThrowIfNull(stageRunStore);
        ArgumentNullException.ThrowIfNull(transientFaultBus);
        ArgumentNullException.ThrowIfNull(runAsync);

        StageRetryBudget effective = retryBudget ?? StageRetryBudget.Default;

        StageRunRecord stageRun = await StartAsync(stageRunStore, projectId, stageName, cancellationToken)
            .ConfigureAwait(false);

        int attempt = 1;
        while (true)
        {
            try
            {
                TResult result = await runAsync(stageRun, cancellationToken).ConfigureAwait(false);
                stageRun = await CompleteAsync(
                        stageRunStore,
                        stageRun,
                        runtimeReporter,
                        cancellationToken,
                        runtimePlanningPreferences)
                    .ConfigureAwait(false);
                return (stageRun, result);
            }
            catch (OperationCanceledException)
            {
                PublishTransient(stageRun, projectId, stageName, TransientFailureKind.UserCancellation,
                    "Stage canceled by caller.", attempt, transientFaultBus);
                await CancelAsync(
                        stageRunStore,
                        stageRun,
                        runtimeReporter,
                        canceledReason,
                        CancellationToken.None,
                        runtimePlanningPreferences,
                        logger)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (TransientFailureClassifier.IsTransient(ex))
            {
                TransientFailureKind kind = TransientFailureClassifier.Classify(ex);
                PublishTransient(stageRun, projectId, stageName, kind, ex.Message,
                    attempt, transientFaultBus, ex);

                if (attempt >= effective.MaxAttempts)
                {
                    await FailAsync(
                            stageRunStore,
                            stageRun,
                            runtimeReporter,
                            BuildRetryExhaustedMessage(ex, attempt),
                            cancellationToken,
                            runtimePlanningPreferences,
                            logger)
                        .ConfigureAwait(false);
                    throw;
                }

                try
                {
                    await Task.Delay(effective.BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during backoff is propagated; mirror the outer arm so the bus
                    // surface is symmetric with the cancel-from-runAsync path.
                    PublishTransient(stageRun, projectId, stageName, TransientFailureKind.UserCancellation,
                        "Stage canceled during retry backoff.", attempt, transientFaultBus);
                    await CancelAsync(
                            stageRunStore,
                            stageRun,
                            runtimeReporter,
                            canceledReason,
                            CancellationToken.None,
                            runtimePlanningPreferences,
                            logger)
                        .ConfigureAwait(false);
                    throw;
                }
                attempt++;
            }
            catch (Exception ex)
            {
                // Non-transient: bubble straight through the retry helper, after marking the row Failed.
                await FailAsync(
                        stageRunStore,
                        stageRun,
                        runtimeReporter,
                        ex.Message,
                        cancellationToken,
                        runtimePlanningPreferences,
                        logger)
                    .ConfigureAwait(false);
                throw;
            }
        }
    }

    private static string BuildRetryExhaustedMessage(Exception ex, int attempt) =>
        $"{ex.GetType().Name}: {ex.Message} (transient retry exhausted after {attempt} attempts)";

    public static async Task<StageRunRecord> CompleteAsync(
        IProjectStageRunStore stageRunStore,
        StageRunRecord stageRun,
        object? runtimeReporter,
        CancellationToken cancellationToken,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    {
        StageRunRecord completed = (await ApplyRuntimeExecutionSummaryAsync(
                stageRun,
                runtimeReporter,
                runtimePlanningPreferences,
                cancellationToken)
                .ConfigureAwait(false))
            .Complete(DateTimeOffset.UtcNow);
        await stageRunStore.UpdateAsync(completed, cancellationToken).ConfigureAwait(false);
        return completed;
    }

    public static async Task<StageRunRecord> FailAsync(
        IProjectStageRunStore stageRunStore,
        StageRunRecord stageRun,
        object? runtimeReporter,
        string failureReason,
        CancellationToken cancellationToken,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null,
        IApplicationLogger? logger = null)
    {
        StageRunRecord failed = (await ApplyRuntimeExecutionSummaryAsync(
                stageRun,
                runtimeReporter,
                runtimePlanningPreferences,
                cancellationToken)
                .ConfigureAwait(false))
            .Fail(DateTimeOffset.UtcNow, failureReason);
        await PersistTerminalAsync(stageRunStore, failed, cancellationToken, logger).ConfigureAwait(false);
        return failed;
    }

    public static async Task<StageRunRecord> CancelAsync(
        IProjectStageRunStore stageRunStore,
        StageRunRecord stageRun,
        object? runtimeReporter,
        string reason,
        CancellationToken cancellationToken,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null,
        IApplicationLogger? logger = null)
    {
        StageRunRecord canceled = (await ApplyRuntimeExecutionSummaryAsync(
                stageRun,
                runtimeReporter,
                runtimePlanningPreferences,
                cancellationToken)
                .ConfigureAwait(false))
            .Cancel(DateTimeOffset.UtcNow, reason);
        await PersistTerminalAsync(stageRunStore, canceled, cancellationToken, logger).ConfigureAwait(false);
        return canceled;
    }

    public static async Task<StageRunRecord> SkipAsync(
        IProjectStageRunStore stageRunStore,
        StageRunRecord stageRun,
        object? runtimeReporter,
        string reason,
        CancellationToken cancellationToken,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null,
        IApplicationLogger? logger = null)
    {
        StageRunRecord skipped = (await ApplyRuntimeExecutionSummaryAsync(
                stageRun,
                runtimeReporter,
                runtimePlanningPreferences,
                cancellationToken)
                .ConfigureAwait(false))
            .Skip(DateTimeOffset.UtcNow, reason);
        await PersistTerminalAsync(stageRunStore, skipped, cancellationToken, logger).ConfigureAwait(false);
        return skipped;
    }

    public static async Task<StageRunRecord> PartiallyCompleteAsync(
        IProjectStageRunStore stageRunStore,
        StageRunRecord stageRun,
        object? runtimeReporter,
        string reason,
        CancellationToken cancellationToken,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null,
        IApplicationLogger? logger = null)
    {
        StageRunRecord partiallyCompleted = (await ApplyRuntimeExecutionSummaryAsync(
                stageRun,
                runtimeReporter,
                runtimePlanningPreferences,
                cancellationToken)
                .ConfigureAwait(false))
            .PartiallyComplete(DateTimeOffset.UtcNow, reason);
        // Use the same 5-second fallback path as other terminal-state helpers so that a partial
        // completion is not silently lost when the cancellation token is already triggered.
        await PersistTerminalAsync(stageRunStore, partiallyCompleted, cancellationToken, logger).ConfigureAwait(false);
        return partiallyCompleted;
    }

    private static async Task PersistTerminalAsync(
        IProjectStageRunStore stageRunStore,
        StageRunRecord terminal,
        CancellationToken cancellationToken,
        IApplicationLogger? logger = null)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await stageRunStore.UpdateAsync(terminal, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Token was cancelled during the write; fall through to timeout retry.
            }
            catch (Exception)
            {
                // Transient store error on first attempt; fall through to timeout-based retry.
            }
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await stageRunStore.UpdateAsync(terminal, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: the fallback write also failed. Log via IApplicationLogger so
            // the warning lands in trackdub.log rather than a Trace sink nobody reads.
            // Must not rethrow — doing so would mask the original stage exception/cancellation.
            string message = $"StageRunHelper: failed to persist terminal status for stage run {terminal.Id} ({terminal.StageName}).";
            logger?.LogWarning(message, ex);
        }
    }

    public static async Task<StageRunRecord> ApplyRuntimeExecutionSummaryAsync(
        StageRunRecord stageRun,
        object? runtimeReporter,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null,
        CancellationToken cancellationToken = default)
    {
        if (runtimeReporter is not IStageRuntimeExecutionReporter reporter ||
            reporter.LastExecutionSummary is not StageRuntimeExecutionSummary summary)
        {
            return stageRun;
        }

        string? benchmarkEvidenceId = null;
        if (runtimePlanningPreferences is not null)
        {
            try
            {
                benchmarkEvidenceId = await runtimePlanningPreferences
                    .GetBenchmarkEvidenceIdAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort metadata only; do not block terminal stage persistence.
            }
        }

        return stageRun.WithRuntimeInfo(
            summary.RequestedProvider,
            summary.SelectedProvider,
            summary.ModelId,
            summary.ModelAlias,
            summary.ModelVariant,
            summary.BootstrapDetail,
            benchmarkEvidenceId: benchmarkEvidenceId);
    }
}
