using Trackdub.Application.Dubbing;

namespace Trackdub.Benchmarks;

public static class ControlledStageBenchmarkMatrixOptionsParser
{
    public static bool TryParse(
        string[] args,
        TextWriter error,
        out ControlledStageBenchmarkMatrixOptions? options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(error);
        options = null;

        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            return false;
        }

        string fixturePath = args[0];
        string? stages = null;
        var shared = new ControlledBenchmarkCliOptions();
        var modelOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 1; index < args.Length; index++)
        {
            if (ControlledBenchmarkCliBinder.TryApplyFlag(args[index], shared))
            {
                continue;
            }

            string option = args[index];
            if (!ControlledBenchmarkCliBinder.TryReadValue(args, ref index, error, out string value))
            {
                return false;
            }

            ControlledCliOptionResult sharedResult =
                ControlledBenchmarkCliBinder.TryApplyOption(option, value, shared, error);
            if (sharedResult == ControlledCliOptionResult.Failed)
            {
                return false;
            }

            if (sharedResult == ControlledCliOptionResult.Applied)
            {
                continue;
            }

            switch (option)
            {
                case "--stages": stages = value; break;
                case "--model":
                    if (!TryParseModelOverride(value, error, modelOverrides))
                    {
                        return false;
                    }

                    break;
                default:
                    error.WriteLine($"Unknown option {option}.");
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(shared.OutputDirectory))
        {
            error.WriteLine("--output is required.");
            return false;
        }

        IReadOnlyList<string>? selectedStages = ParseStages(stages, error);
        if (selectedStages is null)
        {
            return false;
        }

        options = new ControlledStageBenchmarkMatrixOptions
        {
            FixturePath = fixturePath,
            OutputDirectory = shared.OutputDirectory,
            Stages = selectedStages,
            ExpectedFixtureSha256 = shared.ExpectedFixtureSha256,
            TargetLanguage = shared.TargetLanguage,
            SourceLanguage = shared.SourceLanguage,
            Mode = shared.Mode,
            ReuseEngineCache = shared.ReuseEngineCache,
            TranslationStreaming = shared.TranslationStreaming,
            ModelDirectory = shared.ModelDirectory,
            Provider = shared.Provider,
            FfmpegPath = shared.FfmpegPath,
            FfprobePath = shared.FfprobePath,
            ModelOverrides = modelOverrides,
            RunCount = shared.RunCount,
            ResourceTelemetryBounds = shared.ResourceTelemetryBounds,
            Mock = shared.Mock,
            DryRun = shared.DryRun,
        };
        return true;
    }

    private static IReadOnlyList<string>? ParseStages(string? value, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var stages = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] unknown = stages
            .Where(stage => !DubbingPipelineStages.ExtendedStageOrder.Contains(
                stage, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (unknown.Length == 0)
        {
            return stages;
        }

        error.WriteLine(
            $"Unknown pipeline stage(s): {string.Join(", ", unknown)}. "
            + $"Choose from: {string.Join(", ", DubbingPipelineStages.ExtendedStageOrder)}.");
        return null;
    }

    private static bool TryParseModelOverride(
        string value,
        TextWriter error,
        IDictionary<string, string> overrides)
    {
        foreach (string item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = item.IndexOf('=');
            if (separator <= 0 || separator == item.Length - 1)
            {
                error.WriteLine($"--model expects stage=alias entries; got '{item}'.");
                return false;
            }

            string stage = item[..separator].Trim();
            string alias = item[(separator + 1)..].Trim();
            if (!DubbingPipelineStages.ExtendedStageOrder.Contains(stage, StringComparer.OrdinalIgnoreCase))
            {
                error.WriteLine($"Unknown pipeline stage in --model: '{stage}'.");
                return false;
            }

            if (!overrides.TryAdd(stage, alias))
            {
                error.WriteLine($"Duplicate --model entry for stage '{stage}'.");
                return false;
            }
        }

        return true;
    }
}
