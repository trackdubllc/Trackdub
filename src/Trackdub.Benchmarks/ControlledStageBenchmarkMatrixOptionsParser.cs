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
        string? outputDirectory = null;
        string? stages = null;
        string? expectedSha256 = null;
        string targetLanguage = "es";
        string? sourceLanguage = null;
        string mode = "fresh-process";
        bool reuseEngineCache = false;
        bool mock = false;
        bool dryRun = false;
        string? modelDirectory = null;
        string? provider = null;
        string? ffmpeg = null;
        string? ffprobe = null;
        int runCount = 1;
        var modelOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] == "--reuse-engine-cache")
            {
                reuseEngineCache = true;
                continue;
            }

            if (args[index] == "--mock")
            {
                mock = true;
                continue;
            }

            if (args[index] == "--dry-run")
            {
                dryRun = true;
                mock = true;
                continue;
            }

            if (index + 1 >= args.Length)
            {
                error.WriteLine($"Missing value for {args[index]}.");
                return false;
            }

            string option = args[index];
            string value = args[++index];
            switch (option)
            {
                case "--output": outputDirectory = value; break;
                case "--stages": stages = value; break;
                case "--sha256": expectedSha256 = value; break;
                case "--language": targetLanguage = value; break;
                case "--source-language": sourceLanguage = value; break;
                case "--mode": mode = value; break;
                case "--model-directory": modelDirectory = value; break;
                case "--provider": provider = value; break;
                case "--ffmpeg": ffmpeg = value; break;
                case "--ffprobe": ffprobe = value; break;
                case "--runs":
                    if (!int.TryParse(value, out int parsedRuns) || parsedRuns <= 0)
                    {
                        error.WriteLine($"Invalid run count '{value}'. Expected a positive integer.");
                        return false;
                    }

                    runCount = parsedRuns;
                    break;
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

        if (string.IsNullOrWhiteSpace(outputDirectory))
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
            OutputDirectory = outputDirectory,
            Stages = selectedStages,
            ExpectedFixtureSha256 = expectedSha256,
            TargetLanguage = targetLanguage,
            SourceLanguage = sourceLanguage,
            Mode = mode,
            ReuseEngineCache = reuseEngineCache,
            ModelDirectory = modelDirectory,
            Provider = provider,
            FfmpegPath = ffmpeg,
            FfprobePath = ffprobe,
            ModelOverrides = modelOverrides,
            RunCount = runCount,
            Mock = mock,
            DryRun = dryRun,
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
