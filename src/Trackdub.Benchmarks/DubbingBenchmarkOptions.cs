namespace Trackdub.Benchmarks;

/// <summary>
/// Options for a single dubbing benchmark run.
/// </summary>
public sealed record DubbingBenchmarkOptions(
    /// <summary>Path to the source media file (required for single-run mode).</summary>
    string InputPath,
    /// <summary>BCP-47 target language code (default: es).</summary>
    string TargetLanguage = "es",
    /// <summary>BCP-47 source language code (null = auto-detect).</summary>
    string? SourceLanguageCode = null,
    /// <summary>
    /// Root directory for reports and project subfolders
    /// (null = project beside the media file; reports use default location).
    /// </summary>
    string? OutputDirectory = null,
    /// <summary>When true, re-execute all stages even if valid artifacts exist.</summary>
    bool ForceRerun = false,
    /// <summary>Show help and exit.</summary>
    bool ShowHelp = false)
{
    /// <summary>
    /// Parses CLI arguments for the single-run <c>dubbing</c> sub-command.
    /// Usage: dubbing &lt;input-path&gt; [--language &lt;code&gt;] [--source-language &lt;code&gt;]
    ///                [--output &lt;dir&gt;] [--force-rerun] [--help]
    /// </summary>
    public static bool TryParse(string[] args, TextWriter error, out DubbingBenchmarkOptions? options)
    {
        options = null;

        if (args.Length == 0)
        {
            error.WriteLine("Error: Input path is required.");
            return false;
        }

        if (args.Any(arg =>
                arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            options = new DubbingBenchmarkOptions(string.Empty, ShowHelp: true);
            return true;
        }

        string inputPath = args[0];
        string targetLanguage = "es";
        string? sourceLanguageCode = null;
        string? outputDirectory = null;
        bool forceRerun = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--language":
                case "-l":
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine("Error: --language requires a value.");
                        return false;
                    }

                    targetLanguage = args[++i];
                    break;

                default:
                    if (!TryParseCommonFlag(
                            args,
                            ref i,
                            error,
                            ref sourceLanguageCode,
                            ref outputDirectory,
                            ref forceRerun,
                            out bool matched))
                    {
                        return false;
                    }

                    if (!matched)
                    {
                        error.WriteLine($"Error: Unknown option '{args[i]}'.");
                        return false;
                    }

                    break;
            }
        }

        if (!File.Exists(inputPath))
        {
            error.WriteLine($"Error: Input file not found: {inputPath}");
            return false;
        }

        options = new DubbingBenchmarkOptions(
            InputPath: inputPath,
            TargetLanguage: targetLanguage,
            SourceLanguageCode: sourceLanguageCode,
            OutputDirectory: outputDirectory,
            ForceRerun: forceRerun);
        return true;
    }

    /// <summary>
    /// Parses shared flags used by both single-run and batch modes:
    /// <c>--source-language</c>, <c>--output</c>, <c>--force-rerun</c>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when a matched flag is missing its value (parse failure).
    /// <see langword="true"/> when the flag was handled or was not a common flag
    /// (<paramref name="matched"/> distinguishes those cases).
    /// </returns>
    internal static bool TryParseCommonFlag(
        string[] args,
        ref int i,
        TextWriter error,
        ref string? sourceLanguageCode,
        ref string? outputDirectory,
        ref bool forceRerun,
        out bool matched)
    {
        matched = true;

        switch (args[i].ToLowerInvariant())
        {
            case "--source-language":
            case "-s":
                if (i + 1 >= args.Length)
                {
                    error.WriteLine("Error: --source-language requires a value.");
                    return false;
                }

                sourceLanguageCode = args[++i];
                return true;

            case "--output":
            case "-o":
                if (i + 1 >= args.Length)
                {
                    error.WriteLine("Error: --output requires a value.");
                    return false;
                }

                outputDirectory = args[++i];
                return true;

            case "--force-rerun":
                forceRerun = true;
                return true;

            default:
                matched = false;
                return true;
        }
    }
}
