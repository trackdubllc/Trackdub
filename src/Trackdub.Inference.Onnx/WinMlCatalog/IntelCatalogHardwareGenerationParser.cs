using System.Text.RegularExpressions;

namespace Trackdub.Inference.Onnx.WinMlCatalog;

/// <summary>
/// Parses Intel CPU/GPU marketing strings against Windows ML catalog OpenVINO minimums
/// (CPU Tiger Lake 11+, GPU Alder Lake 12+, NPU Arrow Lake 15+).
/// </summary>
internal static partial class IntelCatalogHardwareGenerationParser
{
    public const int MinCpuGeneration = 11;
    public const int MinGpuGeneration = 12;
    public const int MinNpuGeneration = 15;

    public static bool TryParseProcessorGeneration(string? processorName, out int generation)
    {
        generation = 0;
        if (string.IsNullOrWhiteSpace(processorName))
        {
            return false;
        }

        if (TryParseExplicitGeneration(processorName, out generation))
        {
            return true;
        }

        if (TryParseCoreUltraModelGeneration(processorName, out generation))
        {
            return true;
        }

        if (TryParseCoreSkuGeneration(processorName, out generation))
        {
            return true;
        }

        return false;
    }

    public static bool TryParseGenerationFromDescription(string? description, out int generation)
    {
        generation = 0;
        return !string.IsNullOrWhiteSpace(description) && TryParseExplicitGeneration(description, out generation);
    }

    public static bool IsIntelArcGraphics(string? description) =>
        !string.IsNullOrWhiteSpace(description) &&
        description.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
        description.Contains("Arc", StringComparison.OrdinalIgnoreCase);

    public static bool IsIntelIntegratedGraphics(string? description) =>
        !string.IsNullOrWhiteSpace(description) &&
        description.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
        (description.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase) ||
         description.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
         description.Contains("Xe Graphics", StringComparison.OrdinalIgnoreCase));

    public static bool IsCoreUltraSeries2(string? processorName) =>
        !string.IsNullOrWhiteSpace(processorName) &&
        CoreUltraSeries2Model().IsMatch(processorName);

    private static bool TryParseExplicitGeneration(string text, out int generation)
    {
        generation = 0;
        Match match = ExplicitGeneration().Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int parsed))
        {
            return false;
        }

        generation = parsed;
        return true;
    }

    private static bool TryParseCoreUltraModelGeneration(string processorName, out int generation)
    {
        generation = 0;
        Match match = CoreUltraModelNumber().Match(processorName);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int modelNumber))
        {
            return false;
        }

        if (modelNumber >= 200)
        {
            generation = MinNpuGeneration;
            return true;
        }

        if (modelNumber >= 100)
        {
            generation = 14;
            return true;
        }

        return false;
    }

    private static bool TryParseCoreSkuGeneration(string processorName, out int generation)
    {
        generation = 0;
        Match match = CoreSkuNumber().Match(processorName);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int sku))
        {
            return false;
        }

        int inferred = sku >= 1000 ? sku / 1000 : sku / 100;
        if (inferred is < 6 or > 99)
        {
            return false;
        }

        generation = inferred;
        return true;
    }

    [GeneratedRegex(@"\b(\d{1,2})\s*(?:st|nd|rd|th)?\s*Gen\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitGeneration();

    [GeneratedRegex(@"Core\s*(?:\(TM\))?\s*Ultra(?:\s*\d+)?\s+(\d{3,5})[A-Z]?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoreUltraModelNumber();

    [GeneratedRegex(@"Core\s*(?:\(TM\))?\s*Ultra(?:\s*\d+)?\s+(2\d{2,3})[A-Z]?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoreUltraSeries2Model();

    [GeneratedRegex(@"[iR]\s*\d[\s-]*(\d{4,5})[A-Z]?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoreSkuNumber();
}
