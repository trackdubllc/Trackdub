namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Provides access to the current pipeline run's <see cref="DeviceExclusionSet"/>.
/// Implementations manage the lifecycle of exclusion sets across pipeline runs.
/// </summary>
public interface IPipelineDeviceExclusionProvider
{
    /// <summary>
    /// Gets the current pipeline run's device exclusion set.
    /// Returns null if no pipeline run is active.
    /// </summary>
    DeviceExclusionSet? CurrentExclusions { get; }

    /// <summary>
    /// Begins a new pipeline run, creating a fresh <see cref="DeviceExclusionSet"/>.
    /// Returns the exclusion set for the run.
    /// </summary>
    DeviceExclusionSet BeginRun();

    /// <summary>
    /// Ends the current pipeline run, clearing all device exclusions.
    /// Safe to call even if no run is active.
    /// </summary>
    void EndRun();
}
