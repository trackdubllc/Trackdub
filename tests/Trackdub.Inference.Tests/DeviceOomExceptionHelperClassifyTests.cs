using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class DeviceOomExceptionHelperClassifyTests
{
    [Fact]
    public void ClassifyDeviceException_OomMessage_ReturnsMemoryExhausted()
    {
        var exception = new Exception("[ErrorCode:RuntimeException] CUDA error: out of memory");

        DeviceDegradationKind? kind = DeviceOomExceptionHelper.ClassifyDeviceException(exception);

        Assert.Equal(DeviceDegradationKind.MemoryExhausted, kind);
    }

    [Fact]
    public void ClassifyDeviceException_DeviceRemovedMessage_ReturnsDeviceFailed()
    {
        var exception = new Exception("[ErrorCode:RuntimeException] DXGI_ERROR_DEVICE_REMOVED");

        DeviceDegradationKind? kind = DeviceOomExceptionHelper.ClassifyDeviceException(exception);

        Assert.Equal(DeviceDegradationKind.DeviceFailed, kind);
    }

    [Fact]
    public void ClassifyDeviceException_UnrelatedException_ReturnsNull()
    {
        var exception = new InvalidOperationException("boom");

        DeviceDegradationKind? kind = DeviceOomExceptionHelper.ClassifyDeviceException(exception);

        Assert.Null(kind);
    }

    [Fact]
    public void ClassifyDeviceException_MessageWithoutRuntimeExceptionMarker_ReturnsNull()
    {
        var exception = new Exception("out of memory allocating buffer");

        DeviceDegradationKind? kind = DeviceOomExceptionHelper.ClassifyDeviceException(exception);

        Assert.Null(kind);
    }

    // Strings are exactly what the bundled cudart64_12.dll returns from cudaGetErrorString /
    // cudaGetErrorName; CUDA documents these as leaving the process's CUDA state unusable.
    [Theory]
    [InlineData("[ErrorCode:RuntimeException] CUDA failure 700: an illegal memory access was encountered")]
    [InlineData("[ErrorCode:EPFail] [NvTensorRTRTX EP] cudaErrorIllegalAddress")]
    [InlineData("[ErrorCode:Fail] the launch timed out and was terminated")]
    [InlineData("[ErrorCode:EPFail] device-side assert triggered")]
    [InlineData("[ErrorCode:RuntimeException] unspecified launch failure")]
    [InlineData("[ErrorCode:EPFail] CUDA_ERROR_CONTAINED")]
    [InlineData("[ErrorCode:EPFail] CUDA_ERROR_HARDWARE_STACK_ERROR")]
    [InlineData("[ErrorCode:EPFail] CUDA_ERROR_ILLEGAL_INSTRUCTION")]
    [InlineData("[ErrorCode:EPFail] CUDA_ERROR_MISALIGNED_ADDRESS")]
    [InlineData("[ErrorCode:EPFail] CUDA_ERROR_INVALID_ADDRESS_SPACE")]
    [InlineData("[ErrorCode:EPFail] CUDA_ERROR_INVALID_PC")]
    public void ClassifyDeviceExceptionMessage_StickyCudaError_ReturnsDeviceFailedRegardlessOfOrtErrorCode(string message)
    {
        Assert.Equal(
            DeviceDegradationKind.DeviceFailed,
            DeviceOomExceptionHelper.ClassifyDeviceExceptionMessage(message));
    }

    [Fact]
    public void ClassifyDeviceExceptionMessage_EpFailWithoutStickyCudaMarker_ReturnsNull()
    {
        Assert.Null(DeviceOomExceptionHelper.ClassifyDeviceExceptionMessage(
            "[ErrorCode:EPFail] [NvTensorRTRTX EP] Failed to create serialized engine for fused node"));
    }
}
