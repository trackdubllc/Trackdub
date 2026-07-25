namespace Trackdub.Sdk;

/// <summary>
/// Outcome record for a single file processed in a batch run.
/// </summary>
public sealed record BatchFileOutcome
{
    /// <summary>Full path to the media file that was processed.</summary>
    public required string FilePath { get; init; }

    /// <summary>Outcome status of this file.</summary>
    public required BatchFileStatus Status { get; init; }

    /// <summary>Diagnostic reason for failure or skip. Null on success.</summary>
    public string? Reason { get; init; }
}
