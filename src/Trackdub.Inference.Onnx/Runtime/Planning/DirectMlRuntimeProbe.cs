using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Runtime.Planning;

/// <summary>
/// Checks whether the onnxruntime.dll loaded in this process can append DirectML at all.
/// Windows ML's runtime exposes DirectML as a catalog device; standalone DirectML builds export
/// <c>OrtSessionOptionsAppendExecutionProvider_DML</c>; ORT CPU and GPU builds have neither.
/// </summary>
internal static class DirectMlRuntimeProbe
{
    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    /// <summary>Null when DirectML can be appended; otherwise why it cannot. Cached per process.</summary>
    public static string? GetUnavailableReason() => UnavailableReason.Value;

    private static string? Probe()
    {
        if (!OnnxRuntimeBuildCapabilities.SupportsWindowsMlRoutes)
        {
            return "This build has no Windows ML routes, so DirectML cannot be appended.";
        }

        using var catalogOptions = new SessionOptions();
        if (OnnxExecutionSessionFactory.TryAppendDirectMlProvider(catalogOptions, out string? catalogFailure))
        {
            return null;
        }

        using var directOptions = new SessionOptions();
        if (OnnxExecutionSessionFactory.TryAppendDirectMlProviderDirect(directOptions, out string? directFailure))
        {
            return null;
        }

        return $"The loaded ONNX Runtime ({OrtEnv.Instance().GetVersionString()}) cannot run DirectML: " +
               $"catalog: {catalogFailure ?? "n/a"}; direct: {directFailure ?? "n/a"}.";
    }
}
