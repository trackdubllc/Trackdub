using Trackdub.Inference.Onnx.Migraphx;

namespace Trackdub.Inference.Onnx.Runtime.Planning;

public interface ILinuxNativeGpuRuntimeProbe
{
    bool IsNvidiaDriverLoaded();

    bool IsAmdGpuPresent();

    bool IsNativeTensorRtAvailable();

    bool IsCudaOrtProviderAvailable();

    bool IsMigraphxOrtProviderAvailable();
}

public sealed class LinuxNativeGpuRuntimeProbe : ILinuxNativeGpuRuntimeProbe
{
    public bool IsNvidiaDriverLoaded() =>
        Directory.Exists("/proc/driver/nvidia/gpus");

    public bool IsAmdGpuPresent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            if (Directory.Exists("/sys/module/amdgpu"))
            {
                return true;
            }

            const string drmRoot = "/sys/class/drm";
            if (!Directory.Exists(drmRoot))
            {
                return false;
            }

            foreach (string cardPath in Directory.EnumerateDirectories(drmRoot, "card*"))
            {
                string vendorPath = Path.Combine(cardPath, "device", "vendor");
                if (!File.Exists(vendorPath))
                {
                    continue;
                }

                string vendor = File.ReadAllText(vendorPath).Trim();
                if (vendor.Equals("0x1002", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public bool IsNativeTensorRtAvailable() => NativeTensorRtLibraryProbe.IsNativeTensorRtAvailable();

    public bool IsCudaOrtProviderAvailable() => CudaOrtProbe.IsCudaProviderListed();

    public bool IsMigraphxOrtProviderAvailable() =>
        MigraphxOrtProbe.IsProviderListed();

}
