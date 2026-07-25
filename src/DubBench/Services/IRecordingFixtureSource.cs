namespace DubBench.Services;

/// <summary>
/// Describes the result of a recording capture operation.
/// </summary>
public sealed record RecordingResult(
    string OutputPath,
    TimeSpan Duration,
    int SampleRate,
    int Channels);

/// <summary>
/// Source for capturing live recording fixtures (webcam/mic)
/// used by the Dubbing benchmark tab.
/// </summary>
public interface IRecordingFixtureSource
{
    /// <summary>Whether a recording fixture is available on this platform.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Capture a short recording fixture for dubbing estimation.
    /// Returns the path to the captured media file, or null if capture failed.
    /// </summary>
    Task<RecordingResult?> CaptureAsync(
        string outputDir,
        TimeSpan? maxDuration = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probe availability of recording hardware.
    /// </summary>
    Task<bool> ProbeAvailabilityAsync(CancellationToken cancellationToken = default);
}
