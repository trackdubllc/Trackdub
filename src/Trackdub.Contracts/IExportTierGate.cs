namespace Trackdub.Contracts;

/// <summary>
/// Checks tier gates before export begins. The public core ships no concrete
/// implementation; a consuming product supplies its own tier policy.
/// </summary>
public interface IExportTierGate
{
    /// <summary>
    /// Returns true if the watermark should be applied to exported video.
    /// </summary>
    bool RequiresWatermark { get; }

    /// <summary>
    /// Checks if the given media duration is allowed for export.
    /// Returns null if allowed, or an error message if blocked.
    /// </summary>
    string? CheckDurationGate(TimeSpan sourceDuration);
}
