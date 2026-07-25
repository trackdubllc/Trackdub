using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Runtime.Migraphx;

/// <summary>
/// Windows ML MIGraphX EP does not support GenAI scenarios; keep ONNX Runtime models only.
/// </summary>
public static class MigraphxModelSupport
{
    private static readonly string[] UnsupportedModelAliasFragments =
    [
        "genai",
        "ort-genai",
        "phi-",
        "whisper-tiny-genai"
    ];

    public static bool SupportsModel(string? modelAlias, string? engineFamily)
    {
        if (ContainsUnsupportedFragment(modelAlias) || ContainsUnsupportedFragment(engineFamily))
        {
            return false;
        }

        return true;
    }

    public static bool SupportsEntry(BundledModelManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!SupportsModel(entry.ModelId, entry.EngineFamily))
        {
            return false;
        }

        foreach (string alias in entry.Aliases)
        {
            if (!SupportsModel(alias, entry.EngineFamily))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsUnsupportedFragment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        foreach (string fragment in UnsupportedModelAliasFragments)
        {
            if (normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
