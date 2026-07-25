namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// Tracks stage completion state for the current session and provides a health summary.
/// </summary>
public interface IAppHealthMonitor
{
    /// <summary>Records that the named stage completed successfully in this session.</summary>
    void RecordStageCompleted(string stageName);

    /// <summary>Records that the named stage failed in this session.</summary>
    void RecordStageFailed(string stageName, FailureCategory category, string? reason = null);

    /// <summary>Returns a point-in-time summary of the current session health.</summary>
    AppHealthSummary GetHealthSummary();
}
