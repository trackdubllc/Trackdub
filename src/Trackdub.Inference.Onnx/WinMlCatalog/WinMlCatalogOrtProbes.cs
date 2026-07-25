using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Runtime.WinMlCatalog;

namespace Trackdub.Inference.Onnx.WinMlCatalog;

internal static class OpenVinoCatalogOrtProbe
{
    public static bool IsProviderListed() =>
        WinMlCatalogOrtProbeCore.IsListed(OpenVinoCatalogProviderConstants.OrtExecutionProviderName);
}

internal static class QnnOrtProbe
{
    public static bool IsProviderListed() =>
        WinMlCatalogOrtProbeCore.IsListed(QnnProviderConstants.OrtExecutionProviderName);
}

internal static class VitisAiOrtProbe
{
    public static bool IsProviderListed() =>
        WinMlCatalogOrtProbeCore.IsListed(VitisAiProviderConstants.OrtExecutionProviderName);
}

internal static class WinMlCatalogOrtProbeCore
{
    public static bool IsListed(string ortExecutionProviderName)
    {
        try
        {
            return OrtEnv.Instance()
                .GetEpDevices()
                .Any(device =>
                    string.Equals(device.EpName, ortExecutionProviderName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
