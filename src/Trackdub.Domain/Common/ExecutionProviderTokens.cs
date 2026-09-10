namespace Trackdub.Domain;

/// <summary>
/// Shared CLI / manifest vocabulary for <see cref="ExecutionProviderKind"/> pins.
/// One primary tag per kind plus documented aliases; <c>auto</c> means no pin.
/// </summary>
public static class ExecutionProviderTokens
{
    /// <summary>Primary CLI tags including <c>auto</c>.</summary>
    public static IReadOnlyList<string> CliTags { get; } =
    [
        "auto",
        "cpu",
        "dnnl",
        "directml",
        "trt-rtx",
        "cuda",
        "tensorrt",
        "migraphx",
        "qnn",
        "vitisai",
        "openvino",
        "openvino-catalog",
        "coreml",
    ];

    /// <summary>
    /// All tokens accepted by CLI parsers (primary tags + aliases), excluding <c>auto</c>.
    /// </summary>
    public static IReadOnlyList<string> AcceptedProviderTokens { get; } =
    [
        "cpu",
        "dnnl",
        "onednn",
        "onnxruntime-dnnl",
        "directml",
        "dml",
        "trt-rtx",
        "tensorrt-rtx",
        "cuda",
        "tensorrt",
        "migraphx",
        "rocm",
        "qnn",
        "vitisai",
        "openvino",
        "openvino-catalog",
        "coreml",
    ];

    /// <summary>
    /// Tokens accepted by <c>--execution-provider</c> including <c>auto</c> and aliases.
    /// </summary>
    public static IReadOnlyList<string> CliAcceptedTokens { get; } =
        ["auto", .. AcceptedProviderTokens];

    public static string FormatSupportedCliTags() => string.Join(", ", CliTags);

    /// <summary>
    /// Parses a provider token into a kind. Does not accept <c>auto</c>.
    /// </summary>
    public static bool TryParse(string? token, out ExecutionProviderKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        kind = token.Trim().ToLowerInvariant() switch
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
            "coreml" => ExecutionProviderKind.CoreMl,
            _ => default
        };

        return kind != default;
    }

    /// <summary>
    /// Parses a CLI token. Empty or <c>auto</c> yields <c>null</c> (planner chooses).
    /// </summary>
    public static bool TryParseCli(string? token, out ExecutionProviderKind? kind)
    {
        kind = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        if (token.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryParse(token, out ExecutionProviderKind parsed))
        {
            return false;
        }

        kind = parsed;
        return true;
    }

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

        return TryParse(token, out _);
    }

    /// <summary>Primary CLI / discoverability tag for a kind.</summary>
    public static string ToCanonicalTag(ExecutionProviderKind kind) =>
        kind switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.Dnnl => "dnnl",
            ExecutionProviderKind.DirectMl => "directml",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.TensorRt => "tensorrt",
            ExecutionProviderKind.TensorRTRtx => "trt-rtx",
            ExecutionProviderKind.Migraphx => "migraphx",
            ExecutionProviderKind.OpenVino => "openvino",
            ExecutionProviderKind.OpenVinoCatalog => "openvino-catalog",
            ExecutionProviderKind.Qnn => "qnn",
            ExecutionProviderKind.VitisAi => "vitisai",
            ExecutionProviderKind.CoreMl => "coreml",
            _ => kind.ToString().ToLowerInvariant()
        };

    /// <summary>
    /// Manifest / Olive spelling. DNNL uses the expected-runtime form <c>onnxruntime-dnnl</c>.
    /// </summary>
    public static string ToManifestToken(ExecutionProviderKind kind) =>
        kind switch
        {
            ExecutionProviderKind.Dnnl => "onnxruntime-dnnl",
            _ => ToCanonicalTag(kind)
        };

    public static IReadOnlyList<string> GetAliases(ExecutionProviderKind kind) =>
        kind switch
        {
            ExecutionProviderKind.Dnnl => ["onednn", "onnxruntime-dnnl"],
            ExecutionProviderKind.DirectMl => ["dml"],
            ExecutionProviderKind.TensorRTRtx => ["tensorrt-rtx"],
            ExecutionProviderKind.Migraphx => ["rocm"],
            _ => []
        };
}
