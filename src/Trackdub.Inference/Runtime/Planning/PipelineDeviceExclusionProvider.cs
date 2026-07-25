using Microsoft.Extensions.Logging;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Manages the per-pipeline-run <see cref="DeviceExclusionSet"/> lifecycle.
/// Creates a fresh exclusion set at the start of each pipeline run and clears
/// exclusions when the run completes (success or failure).
/// Thread-safe for concurrent access from multiple stage handlers.
/// </summary>
public sealed class PipelineDeviceExclusionProvider(ILogger<PipelineDeviceExclusionProvider>? logger = null)
    : IPipelineDeviceExclusionProvider, Trackdub.Contracts.Pipeline.IPipelineRunLifecycle
{
    private readonly object _lock = new();
    private DeviceExclusionSet? _currentExclusions;

    /// <inheritdoc />
    public DeviceExclusionSet? CurrentExclusions
    {
        get
        {
            lock (_lock)
            {
                return _currentExclusions;
            }
        }
    }

    /// <inheritdoc />
    public DeviceExclusionSet BeginRun()
    {
        lock (_lock)
        {
            if (_currentExclusions is not null)
            {
                logger?.LogWarning("BeginRun called while a previous pipeline run was still active. Clearing previous exclusions.");
                _currentExclusions.ClearRunExclusions();
            }

            _currentExclusions = new DeviceExclusionSet();
            logger?.LogDebug("Pipeline run started. Device exclusion set created.");
            return _currentExclusions;
        }
    }

    /// <inheritdoc />
    public void EndRun()
    {
        lock (_lock)
        {
            if (_currentExclusions is null)
            {
                return;
            }

            _currentExclusions.ClearRunExclusions();
            _currentExclusions = null;
            logger?.LogDebug("Pipeline run ended. Device exclusions cleared.");
        }
    }

    void Trackdub.Contracts.Pipeline.IPipelineRunLifecycle.BeginRun() => BeginRun();
}
