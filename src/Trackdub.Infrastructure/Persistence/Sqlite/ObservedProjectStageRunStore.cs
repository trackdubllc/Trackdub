using System.Collections.Concurrent;
using System.Diagnostics;
using Trackdub.Contracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

/// <summary>Preserves the project record as authority; local evidence is best effort.</summary>
public sealed class ObservedProjectStageRunStore(
    SqliteProjectStageRunStore inner,
    IBenchmarkEvidenceRepository evidence,
    IApplicationLogger logger) : IProjectStageRunStore
{
    private readonly ConcurrentDictionary<Guid, long> starts = new();

    public async Task CreateAsync(StageRunRecord stageRun, CancellationToken cancellationToken)
    {
        await inner.CreateAsync(stageRun, cancellationToken).ConfigureAwait(false);
        starts[stageRun.Id] = Stopwatch.GetTimestamp();
    }

    public async Task UpdateAsync(StageRunRecord stageRun, CancellationToken cancellationToken)
    {
        await inner.UpdateAsync(stageRun, cancellationToken).ConfigureAwait(false);
        if (stageRun.Status == StageRunStatus.Running) return;

        double? duration = starts.TryRemove(stageRun.Id, out long start)
            ? Stopwatch.GetElapsedTime(start).TotalMilliseconds : null;
        BenchmarkEvidenceStatus status = stageRun.Status switch
        {
            StageRunStatus.Completed => BenchmarkEvidenceStatus.Completed,
            StageRunStatus.PartiallyCompleted => BenchmarkEvidenceStatus.PartiallyCompleted,
            StageRunStatus.Skipped => BenchmarkEvidenceStatus.Skipped,
            StageRunStatus.Canceled => BenchmarkEvidenceStatus.Canceled,
            _ => BenchmarkEvidenceStatus.Failed
        };
        var stage = new BenchmarkEvidenceStage
        {
            Name = stageRun.StageName,
            StageRunId = stageRun.Id,
            Status = status,
            Reason = stageRun.FailureReason,
            StartedAtUtc = stageRun.StartedAtUtc,
            CompletedAtUtc = stageRun.CompletedAtUtc,
            DurationMilliseconds = duration,
            ActualModel = stageRun.RuntimeInfo?.ModelId,
            RequestedProvider = stageRun.RuntimeInfo?.RequestedProvider,
            ActualProvider = stageRun.RuntimeInfo?.SelectedProvider
        };
        var report = new BenchmarkEvidenceReport
        {
            RunId = stageRun.Id,
            Kind = BenchmarkEvidenceKind.Observation,
            Scenario = stageRun.StageName,
            RunMode = "ordinary-stage",
            Status = status,
            Reason = stageRun.FailureReason,
            StartedAtUtc = stageRun.StartedAtUtc,
            CompletedAtUtc = stageRun.CompletedAtUtc ?? DateTimeOffset.UtcNow,
            ActualModel = stageRun.RuntimeInfo?.ModelId,
            RequestedProvider = stageRun.RuntimeInfo?.RequestedProvider,
            ActualProvider = stageRun.RuntimeInfo?.SelectedProvider,
            TimingsMilliseconds = new Dictionary<string, double?> { ["stage"] = duration },
            Stages = [stage]
        };
        try
        {
            await evidence.SaveAsync(report, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not persist local stage observation.", ex);
        }
    }

    public Task<IReadOnlyList<StageRunRecord>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        inner.ListByProjectAsync(projectId, cancellationToken);
}
