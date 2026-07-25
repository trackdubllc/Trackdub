namespace Trackdub.Sdk;

/// <summary>
/// Outcome status of a single file in a batch processing run.
/// </summary>
public enum BatchFileStatus
{
    /// <summary>The file was processed successfully.</summary>
    Success,

    /// <summary>The file failed during pipeline execution.</summary>
    Failed,

    /// <summary>The file was not attempted due to a prior fail-fast halt.</summary>
    Skipped,
}
