using Trackdub.Contracts.Diagnostics;

namespace Trackdub.TestDoubles;

/// <summary>
/// In-memory fake implementation of <see cref="IAppHealthMonitor"/> for use in unit tests.
/// </summary>
public sealed class FakeAppHealthMonitor : IAppHealthMonitor
{
    private readonly List<string> completedStages = [];
    private readonly List<StageFailureRecord> failedStages = [];

    /// <summary>Gets all stage names that were recorded as completed.</summary>
    public IReadOnlyList<string> CompletedStages => completedStages.AsReadOnly();

    /// <summary>Gets all failure records that were recorded.</summary>
    public IReadOnlyList<StageFailureRecord> FailedStages => failedStages.AsReadOnly();

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
    public AppHealthSummary GetHealthSummary() =>
        new AppHealthSummary(
            CompletedStages: completedStages.AsReadOnly(),
            FailedStages: failedStages.AsReadOnly());
}
