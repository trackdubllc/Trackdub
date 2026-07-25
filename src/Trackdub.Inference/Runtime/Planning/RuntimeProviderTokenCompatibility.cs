using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

public static class RuntimeProviderTokenCompatibility
{
    public static IReadOnlyList<string> AllowedVariantProviderTokens { get; } =
    [
        "cpu",
        "dnnl",
        "onednn",
        "dml",
        "directml",
        "cuda",
        "tensorrt",
        "trt-rtx",
        "tensorrt-rtx",
        "migraphx",
        "rocm",
        "openvino",
        "openvino-catalog",
        "qnn",
        "vitisai"
    ];

    public static bool IsKnownProviderToken(string? token, bool allowAuto = false)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (allowAuto && token.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryParseProviderToken(token, out _);
    }

    public static bool TryParseProviderToken(string? token, out ExecutionProviderKind provider)
    {
        provider = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        provider = token.Trim().ToLowerInvariant() switch
        {
            "cpu" => ExecutionProviderKind.Cpu,
            "dnnl" or "onednn" or "onnxruntime-dnnl" => ExecutionProviderKind.Dnnl,
            "dml" or "directml" => ExecutionProviderKind.DirectMl,
            "cuda" => ExecutionProviderKind.Cuda,
            "tensorrt" => ExecutionProviderKind.TensorRt,
            "trt-rtx" or "tensorrt-rtx" => ExecutionProviderKind.TensorRTRtx,
            "migraphx" or "rocm" => ExecutionProviderKind.Migraphx,
            "openvino" => ExecutionProviderKind.OpenVino,
            "openvino-catalog" => ExecutionProviderKind.OpenVinoCatalog,
            "qnn" => ExecutionProviderKind.Qnn,
            "vitisai" => ExecutionProviderKind.VitisAi,
            _ => default
        };

        return provider != default;
    }

    public static string ToManifestToken(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.Dnnl => "onnxruntime-dnnl",
            ExecutionProviderKind.DirectMl => "directml",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.TensorRt => "tensorrt",
            ExecutionProviderKind.TensorRTRtx => "trt-rtx",
            ExecutionProviderKind.Migraphx => "migraphx",
            ExecutionProviderKind.OpenVino => "openvino",
            ExecutionProviderKind.OpenVinoCatalog => "openvino-catalog",
            ExecutionProviderKind.Qnn => "qnn",
            ExecutionProviderKind.VitisAi => "vitisai",
            _ => provider.ToString().ToLowerInvariant()
        };

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
            "onnxruntime-migraphx";
    }
}
