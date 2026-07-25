using System.Text.RegularExpressions;

namespace Trackdub.Infrastructure.ModelOptimization;

public static partial class OliveOptimizationProgress
{
    [GeneratedRegex(@"^\s*step\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StepProgressRegex();

    public static bool TryParseStep(string line, out int current, out int total)
    {
        current = 0;
        total = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        Match match = StepProgressRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        current = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        total = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return current > 0 && total > 0;
    }

    public static bool TryFormatProgressLine(string line, out string progressLine)
    {
        progressLine = string.Empty;
        if (!TryParseStep(line, out _, out _))
        {
            return false;
        }

        progressLine = $"[progress] {line.Trim()}";
        return true;
    }
}
