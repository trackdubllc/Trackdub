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

        return ClassifyDeviceExceptionMessage(exception.Message);
    }

    /// <summary>
    /// Message-only overload of <see cref="ClassifyDeviceException"/>, for callers that hold
    /// the ONNX Runtime error text rather than the exception instance.
    /// </summary>
    public static DeviceDegradationKind? ClassifyDeviceExceptionMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        // Checked before the RuntimeException gate because the CUDA-backed EPs (TensorRT RTX
        // plugin, CUDA) can surface these under other ORT error codes such as EPFail.
        if (IsStickyCudaError(message))
        {
            return DeviceDegradationKind.DeviceFailed;
        }

        if (!message.Contains("[ErrorCode:RuntimeException]", StringComparison.OrdinalIgnoreCase))
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

    // CUDA errors that leave the whole process's CUDA state unusable: every later CUDA call
    // returns the same error until the process restarts, so neither a re-run nor a new CUDA
    // session in this process can recover (CUDA Runtime/Driver API "Error Types"). Runtime names,
    // descriptions, and Driver API names that ORT/EP messages report are listed, taken from the
    // cudart shipped in the TensorRT RTX bundle.
    private static readonly string[] StickyCudaErrorMarkers =
    [
        "cudaErrorIllegalAddress", "an illegal memory access was encountered",
        "cudaErrorLaunchTimeout", "the launch timed out and was terminated",
        "cudaErrorAssert", "device-side assert triggered",
        "cudaErrorHardwareStackError", "hardware stack error",
        "cudaErrorIllegalInstruction", "an illegal instruction was encountered",
        "cudaErrorMisalignedAddress", "misaligned address",
        "cudaErrorInvalidAddressSpace", "operation not supported on global/shared address space",
        "cudaErrorInvalidPc", "invalid program counter",
        "cudaErrorLaunchFailure", "unspecified launch failure",
        "cudaErrorTensorMemoryLeak", "tensor memory not completely freed",
        "cudaErrorContained", "Invalid access of peer GPU memory over nvlink or a hardware error",
        "CUDA_ERROR_ILLEGAL_ADDRESS", "CUDA_ERROR_LAUNCH_TIMEOUT", "CUDA_ERROR_ASSERT",
        "CUDA_ERROR_HARDWARE_STACK_ERROR", "CUDA_ERROR_ILLEGAL_INSTRUCTION",
        "CUDA_ERROR_MISALIGNED_ADDRESS", "CUDA_ERROR_INVALID_ADDRESS_SPACE",
        "CUDA_ERROR_INVALID_PC",
        "CUDA_ERROR_CONTAINED", "CUDA_ERROR_LAUNCH_FAILED",
    ];

    private static bool IsStickyCudaError(string message) =>
        StickyCudaErrorMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
