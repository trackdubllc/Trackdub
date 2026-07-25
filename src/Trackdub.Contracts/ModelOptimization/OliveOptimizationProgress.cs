using System.Text.RegularExpressions;

namespace Trackdub.Contracts.ModelOptimization;

public static partial class OliveOptimizationProgress
{
    [GeneratedRegex(@"Step\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StepPattern();

    public static bool TryParseStep(string line, out int currentStep, out int totalSteps)
    {
        Match match = StepPattern().Match(line);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out currentStep) ||
            !int.TryParse(match.Groups[2].Value, out totalSteps) ||
            totalSteps <= 0 ||
            currentStep < 0)
        {
            currentStep = 0;
            totalSteps = 0;
            return false;
        }

        return true;
    }

    public static string FormatStructuredProgress(int currentStep, int totalSteps) =>
        $"[progress] Step {currentStep}/{totalSteps}";

    public static bool TryFormatProgressLine(string line, out string progressLine)
    {
        if (TryParseStep(line, out int currentStep, out int totalSteps))
        {
            progressLine = FormatStructuredProgress(currentStep, totalSteps);
            return true;
        }

        progressLine = string.Empty;
        return false;
    }
}
