using Trackdub.Domain.Artifacts;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// Factory for creating <see cref="PipelineDegradationRecord"/> instances
/// related to device failures during inference execution.
/// </summary>
public static class DeviceFailureDegradationFactory
{
    /// <summary>
    /// Creates a degradation record for a device failure during inference.
    /// Used when a device is removed, a driver crashes, or inference times out.
    /// </summary>
    public static PipelineDegradationRecord CreateDeviceFailureRecord(
        string stageName,
        int deviceIndex,
        string adapterDescription,
        string errorDetail,
        Guid? stageRunId = null)
    {
        return new PipelineDegradationRecord(
            Stage: stageName,
            Code: "DEVICE_FAILURE",
            Message: $"Device {deviceIndex} ({adapterDescription}) failed during inference and has been excluded until restart.",
            Detail: errorDetail,
            SelectedFallback: "Next ranked device in hardware matrix",
            RecommendedAction: "Restart the application to re-enable the device, or check device driver status.",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            StageRunId: stageRunId);
    }

    /// <summary>
    /// Creates a degradation record for an OOM condition during session creation.
    /// </summary>
    public static PipelineDegradationRecord CreateOomRecord(
        string stageName,
        int deviceIndex,
        string adapterDescription,
        string errorDetail,
        Guid? stageRunId = null)
    {
        return new PipelineDegradationRecord(
            Stage: stageName,
            Code: "DEVICE_OOM",
            Message: $"Device {deviceIndex} ({adapterDescription}) ran out of memory during session creation and has been excluded for this pipeline run.",
            Detail: errorDetail,
            SelectedFallback: "Next ranked device in hardware matrix",
            RecommendedAction: "Close other GPU-intensive applications or use a device with more VRAM.",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            StageRunId: stageRunId);
    }
}
