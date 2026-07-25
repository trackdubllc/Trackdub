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
}
