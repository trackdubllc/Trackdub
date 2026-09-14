using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Manifest / Olive compatibility surface over <see cref="ExecutionProviderTokens"/>.
/// </summary>
public static class RuntimeProviderTokenCompatibility
{
    public static IReadOnlyList<string> AllowedVariantProviderTokens =>
        ExecutionProviderTokens.AcceptedProviderTokens;

    public static bool IsKnownProviderToken(string? token, bool allowAuto = false) =>
        ExecutionProviderTokens.IsKnownProviderToken(token, allowAuto);

    public static bool TryParseProviderToken(string? token, out ExecutionProviderKind provider) =>
        ExecutionProviderTokens.TryParse(token, out provider);

    public static string ToManifestToken(ExecutionProviderKind provider) =>
        ExecutionProviderTokens.ToManifestToken(provider);

    public static bool IsVariantSupportedForProvider(
        IReadOnlyList<string>? supportedProviders,
        ExecutionProviderKind provider)
    {
        if (supportedProviders is null || supportedProviders.Count == 0)
        {
            return true;
        }

        return supportedProviders.Any(token => TokenMatchesProvider(token, provider));
    }

    public static bool IsExpectedRuntimeCompatible(string? expectedRuntime, ExecutionProviderKind provider)
    {
        if (string.IsNullOrWhiteSpace(expectedRuntime))
        {
            return true;
        }

        bool sawRecognizedProviderToken = false;
        foreach (string token in expectedRuntime.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ExpectedRuntimeTokenMatchesProvider(token, provider))
            {
                return true;
            }

            sawRecognizedProviderToken |= IsRecognizedExpectedRuntimeProviderToken(token);
        }

        return !sawRecognizedProviderToken;
    }

    private static bool TokenMatchesProvider(string token, ExecutionProviderKind provider)
    {
        if (TryParseProviderToken(token, out ExecutionProviderKind parsedProvider) &&
            parsedProvider == provider)
        {
            return true;
        }

        return token.Trim().ToLowerInvariant() switch
        {
            "openvino" or "openvino-catalog" => provider is ExecutionProviderKind.OpenVinoCatalog or ExecutionProviderKind.OpenVino,
            _ => false
        };
    }

    private static bool ExpectedRuntimeTokenMatchesProvider(string token, ExecutionProviderKind provider)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return token.Trim().ToLowerInvariant() switch
        {
            "onnxruntime-cpu" => provider is ExecutionProviderKind.Cpu,
            "onnxruntime-dnnl" => provider is ExecutionProviderKind.Dnnl,
            "onnxruntime-directml" => provider is ExecutionProviderKind.DirectMl,
            "windows-ml" => provider is ExecutionProviderKind.DirectMl or ExecutionProviderKind.Migraphx
                or ExecutionProviderKind.Qnn or ExecutionProviderKind.OpenVinoCatalog or ExecutionProviderKind.VitisAi,
            "onnxruntime-cuda" or "python-cuda" => provider is ExecutionProviderKind.Cuda,
            "onnxruntime-tensorrt" => provider is ExecutionProviderKind.TensorRt,
            "tensorrt-rtx" or "trt-rtx" => provider is ExecutionProviderKind.TensorRTRtx,
            "onnxruntime-migraphx" => provider is ExecutionProviderKind.Migraphx,
            "onnxruntime-coreml" or "coreml" => provider is ExecutionProviderKind.CoreMl,
            _ => false
        };
    }

    private static bool IsRecognizedExpectedRuntimeProviderToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return token.Trim().ToLowerInvariant() is
            "onnxruntime-cpu" or
            "onnxruntime-dnnl" or
            "onnxruntime-directml" or
            "windows-ml" or
            "onnxruntime-cuda" or
            "python-cuda" or
            "onnxruntime-tensorrt" or
            "tensorrt-rtx" or
            "trt-rtx" or
            "onnxruntime-migraphx" or
            "onnxruntime-coreml" or
            "coreml";
    }
}
