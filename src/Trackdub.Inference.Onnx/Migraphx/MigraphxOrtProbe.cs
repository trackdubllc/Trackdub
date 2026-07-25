using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Inference.Onnx.Migraphx;

internal static class MigraphxOrtProbe
{
    public static bool IsProviderListed()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders()
                .Any(name => string.Equals(name, MigraphxProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> GetAvailableProviderNames()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders().ToArray();
        }
        catch
        {
            return [];
        }
    }
}
