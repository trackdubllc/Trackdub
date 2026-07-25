using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

internal static class LinuxNvidiaHardwareGate
{
    public static (bool Eligible, TensorRtRtxReadinessBlocker Blocker, string Detail) Evaluate()
    {
        if (!OperatingSystem.IsLinux())
        {
            return (false, TensorRtRtxReadinessBlocker.PlatformUnsupported,
                "TensorRT RTX EP ABI plugin route is Linux-only.");
        }

        var probe = new LinuxNativeGpuRuntimeProbe();
        if (!probe.IsNvidiaDriverLoaded())
        {
            return (false, TensorRtRtxReadinessBlocker.GpuVendorMismatch,
                "No loaded NVIDIA driver detected for TensorRT RTX.");
        }

        return (true, TensorRtRtxReadinessBlocker.None, "Loaded NVIDIA driver detected for TensorRT RTX.");
    }
}
