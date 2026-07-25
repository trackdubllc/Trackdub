using System.Collections.Concurrent;
using Trackdub.Contracts.Diagnostics;

namespace Trackdub.Infrastructure.Diagnostics;

/// <summary>
/// Thread-safe in-process implementation of <see cref="IAppHealthMonitor"/>.
/// Tracks stage completion and failure state for the current session.
/// </summary>
public sealed class AppHealthMonitor : IAppHealthMonitor
{
    private readonly ConcurrentBag<string> completedStages = new();
    private readonly ConcurrentBag<StageFailureRecord> failedStages = new();

    /// <inheritdoc />
    public void RecordStageCompleted(string stageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        completedStages.Add(stageName.Trim());
    }

    /// <inheritdoc />
    public void RecordStageFailed(string stageName, FailureCategory category, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        failedStages.Add(new StageFailureRecord(stageName.Trim(), category, reason));
    }

    /// <inheritdoc />
    public AppHealthSummary GetHealthSummary()
    {
        return new AppHealthSummary(
            CompletedStages: completedStages.ToArray(),
            FailedStages: failedStages.ToArray());
    }
}
