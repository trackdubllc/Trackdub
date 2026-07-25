using Microsoft.Extensions.Logging;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Utility for detecting OOM exceptions from ONNX Runtime session creation
/// and marking devices as memory-exhausted in the current pipeline run's exclusion set.
/// </summary>
public static class DeviceOomExceptionHelper
{
    /// <summary>
    /// Determines whether an exception represents an ONNX Runtime out-of-memory condition.
    /// Checks for "[ErrorCode:RuntimeException]" combined with OOM-related keywords.
    /// </summary>
    public static bool IsOnnxRuntimeOomException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string message = exception.Message;
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        // ONNX Runtime OOM exceptions contain "[ErrorCode:RuntimeException]" and memory-related keywords
        bool hasRuntimeException = message.Contains("[ErrorCode:RuntimeException]", StringComparison.OrdinalIgnoreCase);
        bool hasOomIndicator = message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("insufficient memory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("allocation failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("DXGI_ERROR_DEVICE_REMOVED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("E_OUTOFMEMORY", StringComparison.OrdinalIgnoreCase);

        return hasRuntimeException && hasOomIndicator;
    }

    /// <summary>
    /// If the exception is an OOM condition and a device exclusion provider is available,
    /// marks the device as memory-exhausted. Returns true if the device was marked.
    /// </summary>
    public static bool TryMarkDeviceExhausted(
        Exception exception,
        int? deviceIndex,
        IPipelineDeviceExclusionProvider? exclusionProvider,
        ILogger? logger = null)
    {
        if (deviceIndex is null || exclusionProvider is null)
        {
            return false;
        }

        if (!IsOnnxRuntimeOomException(exception))
        {
            return false;
        }

        DeviceExclusionSet? exclusions = exclusionProvider.CurrentExclusions;
        if (exclusions is null)
        {
            return false;
        }

        exclusions.MarkMemoryExhausted(deviceIndex.Value);
        logger?.LogWarning(
            "Device {DeviceIndex} marked as memory-exhausted due to OOM during session creation: {Message}",
            deviceIndex.Value,
            exception.Message);
        return true;
    }

    /// <summary>
    /// Marks a device as failed due to an inference-time failure (device removed, driver crash,
    /// or timeout). Returns true if the device was marked.
    /// </summary>
    public static bool TryMarkDeviceFailed(
        int? deviceIndex,
        string reason,
        IPipelineDeviceExclusionProvider? exclusionProvider,
        ILogger? logger = null)
    {
        if (deviceIndex is null || exclusionProvider is null)
        {
            return false;
        }

        DeviceExclusionSet? exclusions = exclusionProvider.CurrentExclusions;
        if (exclusions is null)
        {
            return false;
        }

        exclusions.MarkFailed(deviceIndex.Value, reason);
        logger?.LogWarning(
            "Device {DeviceIndex} marked as failed and excluded until restart. Reason: {Reason}",
            deviceIndex.Value,
            reason);
        return true;
    }

    /// <summary>
    /// Classifies an ONNX Runtime device exception as memory exhaustion vs a device failure
    /// (device removed / driver crash). Returns null when the exception is not a recognized
    /// device-level failure and must be allowed to propagate unchanged.
    /// </summary>
    public static DeviceDegradationKind? ClassifyDeviceException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string message = exception.Message;
        if (string.IsNullOrEmpty(message)
            || !message.Contains("[ErrorCode:RuntimeException]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        bool isDeviceFailure = message.Contains("DXGI_ERROR_DEVICE_REMOVED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("device removed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("device lost", StringComparison.OrdinalIgnoreCase)
            || message.Contains("device hung", StringComparison.OrdinalIgnoreCase);
        if (isDeviceFailure)
        {
            return DeviceDegradationKind.DeviceFailed;
        }

        bool isOom = message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("insufficient memory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("allocation failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("E_OUTOFMEMORY", StringComparison.OrdinalIgnoreCase);
        return isOom ? DeviceDegradationKind.MemoryExhausted : null;
    }
}
