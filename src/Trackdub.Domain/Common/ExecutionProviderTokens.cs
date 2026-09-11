namespace Trackdub.Domain;

/// <summary>
/// Shared CLI / manifest vocabulary for <see cref="ExecutionProviderKind"/> pins.
/// One primary tag per kind plus documented aliases; <c>auto</c> means no pin.
/// </summary>
public static class ExecutionProviderTokens
{
    public static IReadOnlyList<string> CliTags { get; } = BuildCliTags();

    public static IReadOnlyList<string> AcceptedProviderTokens { get; } = BuildAcceptedTokens();

    public static IReadOnlyList<string> CliAcceptedTokens { get; } =
        ["auto", .. AcceptedProviderTokens];

    public static string FormatSupportedCliTags() => string.Join(", ", CliTags);

    private static readonly Dictionary<string, ExecutionProviderKind> ParseMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cpu"] = ExecutionProviderKind.Cpu,
            ["dnnl"] = ExecutionProviderKind.Dnnl,
            ["onednn"] = ExecutionProviderKind.Dnnl,
            ["onnxruntime-dnnl"] = ExecutionProviderKind.Dnnl,
            ["dml"] = ExecutionProviderKind.DirectMl,
            ["directml"] = ExecutionProviderKind.DirectMl,
            ["cuda"] = ExecutionProviderKind.Cuda,
            ["tensorrt"] = ExecutionProviderKind.TensorRt,
            ["trt-rtx"] = ExecutionProviderKind.TensorRTRtx,
            ["tensorrt-rtx"] = ExecutionProviderKind.TensorRTRtx,
            ["migraphx"] = ExecutionProviderKind.Migraphx,
            ["rocm"] = ExecutionProviderKind.Migraphx,
            ["openvino"] = ExecutionProviderKind.OpenVino,
            ["openvino-catalog"] = ExecutionProviderKind.OpenVinoCatalog,
            ["qnn"] = ExecutionProviderKind.Qnn,
            ["vitisai"] = ExecutionProviderKind.VitisAi,
            ["coreml"] = ExecutionProviderKind.CoreMl,
        };

    public static bool TryParse(string? token, out ExecutionProviderKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return ParseMap.TryGetValue(token.Trim(), out kind);
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

    /// <summary>
    /// Warning emitted when a user-facing <c>cuda</c> pin is remapped on Windows.
    /// </summary>
    public const string WindowsCudaRemapWarning =
        "Warning: execution provider cuda on Windows maps to TensorRT RTX (trt-rtx). "
        + "Use trt-rtx explicitly, or run on Linux for native CUDA.";

    /// <summary>
    /// Applies platform policy to a parsed execution-provider pin.
    /// On Windows, <see cref="ExecutionProviderKind.Cuda"/> maps to
    /// <see cref="ExecutionProviderKind.TensorRTRtx"/> (NVIDIA compatibility alias).
    /// </summary>
    public static ExecutionProviderKind ResolvePlatformPin(ExecutionProviderKind kind) =>
        kind is ExecutionProviderKind.Cuda && OperatingSystem.IsWindows()
            ? ExecutionProviderKind.TensorRTRtx
            : kind;

    /// <summary>
    /// Resolves <paramref name="kind"/> via <see cref="ResolvePlatformPin"/> and reports when
    /// a Windows <c>cuda</c> remap occurred.
    /// </summary>
    public static ExecutionProviderKind ResolvePlatformPin(
        ExecutionProviderKind kind,
        out string? platformRemapWarning)
    {
        ExecutionProviderKind resolved = ResolvePlatformPin(kind);
        platformRemapWarning = kind is ExecutionProviderKind.Cuda &&
            resolved is ExecutionProviderKind.TensorRTRtx
                ? WindowsCudaRemapWarning
                : null;
        return resolved;
    }

    /// <summary>
    /// Returns whether two pins are equivalent after <see cref="ResolvePlatformPin"/>.
    /// </summary>
    public static bool PlatformPinsEquivalent(
        ExecutionProviderKind left,
        ExecutionProviderKind right) =>
        ResolvePlatformPin(left) == ResolvePlatformPin(right);

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

    private static IReadOnlyList<string> BuildCliTags()
    {
        var tags = new List<string> { "auto" };
        foreach (ExecutionProviderKind kind in Enum.GetValues<ExecutionProviderKind>())
        {
            if (kind == default) continue;
            tags.Add(ToCanonicalTag(kind));
        }
        return tags;
    }

    private static IReadOnlyList<string> BuildAcceptedTokens()
    {
        var tokens = new List<string>();
        foreach (ExecutionProviderKind kind in Enum.GetValues<ExecutionProviderKind>())
        {
            if (kind == default) continue;
            tokens.Add(ToCanonicalTag(kind));
            tokens.AddRange(GetAliases(kind));
        }
        return tokens;
    }
}
