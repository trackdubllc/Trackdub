using Microsoft.ML.OnnxRuntime;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif

namespace Trackdub.Inference.Onnx.Dnnl;

internal static class DnnlOrtProbe
{
    public const string OrtExecutionProviderName = "DnnlExecutionProvider";
    public const string OrtExecutionProviderNameUpper = "DNNLExecutionProvider";

    public static bool IsProviderListed()
    {
        try
        {
            EnsureOrtNativeResolverInitialized();
            return OrtEnv.Instance().GetAvailableProviders().Any(IsDnnlExecutionProviderName);
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureOrtNativeResolverInitialized()
    {
#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
    }

    public static bool IsDnnlExecutionProviderName(string? providerName) =>
        string.Equals(providerName, OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(providerName, OrtExecutionProviderNameUpper, StringComparison.OrdinalIgnoreCase);
}
