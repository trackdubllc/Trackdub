using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

internal static class TensorRtRtxOrtProbe
{
    public static bool IsPluginProviderListed()
    {
        try
        {
            return OrtEnv.Instance()
                .GetEpDevices()
                .Any(device =>
                    device.HardwareDevice.Type is OrtHardwareDeviceType.GPU &&
                    string.Equals(
                        device.EpName,
                        TensorRtRtxProviderConstants.PluginOrtExecutionProviderName,
                        StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsNativeTensorRtProviderListed()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders()
                .Any(name =>
                    string.Equals(name, TensorRtRtxProviderConstants.NativeOrtExecutionProviderName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
