namespace Trackdub.Benchmarks;

public sealed record AudioPrepBenchmarkOptions(
    string ManifestPath,
    string OutputPath,
    ReportFormat ReportFormat,
    bool ShowHelp)
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        TextWriter errorWriter,
        out AudioPrepBenchmarkOptions options)
    {
        string? manifestPath = null;
        string outputPath = Path.Combine(Environment.CurrentDirectory, "audio-prep-benchmark-report.json");
        var reportFormat = ReportFormat.Both;
        bool showHelp = false;

        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;

                case "--manifest":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out manifestPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }
                    break;

                case "--output":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out outputPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }
                    break;

                case "--format":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string formatText))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    if (!Enum.TryParse(formatText, ignoreCase: true, out reportFormat))
                    {
                        errorWriter.WriteLine($"Unknown format '{formatText}'. Expected console, json, or both.");
                        options = DefaultWithHelp();
                        return false;
                    }
                    break;

                default:
                    errorWriter.WriteLine($"Unknown audio-prep argument '{arg}'.");
                    options = DefaultWithHelp();
                    return false;
            }
        }

        if (showHelp)
        {
            options = new AudioPrepBenchmarkOptions(string.Empty, Path.GetFullPath(outputPath), reportFormat, ShowHelp: true);
            return true;
        }

        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            errorWriter.WriteLine("Missing required argument --manifest <path>.");
            options = DefaultWithHelp();
            return false;
        }

        options = new AudioPrepBenchmarkOptions(
            Path.GetFullPath(manifestPath),
            Path.GetFullPath(outputPath),
            reportFormat,
            ShowHelp: false);
        return true;
    }

    private static AudioPrepBenchmarkOptions DefaultWithHelp() =>
        new(string.Empty, Path.Combine(Environment.CurrentDirectory, "audio-prep-benchmark-report.json"), ReportFormat.Both, ShowHelp: true);

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        TextWriter errorWriter,
        out string value)
    {
        if (index + 1 >= args.Count)
        {
            errorWriter.WriteLine($"Missing value for {optionName}.");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }
}
