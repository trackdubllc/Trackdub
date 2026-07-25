using Trackdub.Contracts;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain;

namespace Trackdub.Application.Transcripts;

internal static class StageRunHygiene
{
    internal const string StaleRunningFailureReason = "process_crashed_or_persist_failed";

    internal static readonly TimeSpan StaleRunningThreshold = TimeSpan.FromMinutes(30);

    internal static async Task<IReadOnlyList<StageRunRecord>> ReconcileStaleRunningAsync(
        IProjectStageRunStore stageRunStore,
        IReadOnlyList<StageRunRecord> stageRuns,
        IApplicationLogger? logger,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? preserveRunIds = null)
    {
        ArgumentNullException.ThrowIfNull(stageRunStore);

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - StaleRunningThreshold;
        if (stageRuns.All(r => r.Status != StageRunStatus.Running || r.StartedAtUtc >= cutoff))
        {
            return stageRuns;
        }

        List<StageRunRecord> reconciled = new(stageRuns.Count);
        foreach (StageRunRecord run in stageRuns)
        {
            if (run.Status == StageRunStatus.Running &&
                run.StartedAtUtc < cutoff &&
                preserveRunIds?.Contains(run.Id) != true)
            {
                StageRunRecord failed = run.Fail(DateTimeOffset.UtcNow, StaleRunningFailureReason);
                await stageRunStore.UpdateAsync(failed, cancellationToken).ConfigureAwait(false);
                logger?.LogWarning(
                    $"Reaped stale stage run {failed.Id} ({failed.StageName}) started at {run.StartedAtUtc:O}.");
                reconciled.Add(failed);
            }
            else
            {
                reconciled.Add(run);
            }
        }

        return reconciled;
    }
}
