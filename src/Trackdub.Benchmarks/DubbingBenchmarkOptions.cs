using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

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
    bool ShowHelp = false,
    /// <summary>
    /// Repeatable per-stage model pins (<c>--model stage=alias</c>). Keys are canonical
    /// stage names (case-insensitive on input).
    /// </summary>
    IReadOnlyDictionary<string, string>? ModelPins = null,
    /// <summary>
    /// Repeatable per-stage execution-provider pins (<c>--provider stage=provider</c>).
    /// Keys are canonical stage names; values are provider labels understood by
    /// <see cref="Trackdub.Domain.ExecutionProviderTokens"/>.
    /// </summary>
    IReadOnlyDictionary<string, string>? ProviderPins = null)
{
    /// <summary>
    /// Parses CLI arguments for the single-run <c>dubbing</c> sub-command.
    /// Usage: dubbing &lt;input-path&gt; [--language &lt;code&gt;] [--source-language &lt;code&gt;]
    ///                [--output &lt;dir&gt;] [--force-rerun]
    ///                [--model &lt;stage&gt;=&lt;alias&gt;] [--provider &lt;stage&gt;=&lt;provider&gt;] [--help]
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
        var modelPins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var providerPins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

                case "--model":
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine("Error: --model requires a value.");
                        return false;
                    }

                    if (!TryParseStagePin(args[++i], "--model", error, modelPins))
                    {
                        return false;
                    }

                    break;

                case "--provider":
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine("Error: --provider requires a value.");
                        return false;
                    }

                    if (!TryParseStagePin(args[++i], "--provider", error, providerPins))
                    {
                        return false;
                    }

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
            ForceRerun: forceRerun,
            ModelPins: modelPins.Count > 0 ? modelPins : null,
            ProviderPins: providerPins.Count > 0 ? providerPins : null);
        return true;
    }

    /// <summary>
    /// Stage keys accepted by <c>--provider</c>: canonical <see cref="StageNames"/>
    /// values that map to runtime inference stages. Case-insensitive input stores the
    /// canonical StageNames spelling.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ProviderPinStageNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [StageNames.Vad] = StageNames.Vad,
            [StageNames.Asr] = StageNames.Asr,
            [StageNames.Diarization] = StageNames.Diarization,
            [StageNames.Separation] = StageNames.Separation,
            [StageNames.OverlapRescue] = StageNames.OverlapRescue,
            [StageNames.Translation] = StageNames.Translation,
            [StageNames.Tts] = StageNames.Tts,
            [StageNames.SpeechEnhancement] = StageNames.SpeechEnhancement,
            [StageNames.TextRefinementAsr] = StageNames.TextRefinementAsr,
            [StageNames.LipSync] = StageNames.LipSync,
            [StageNames.LipSynthesis] = StageNames.LipSynthesis,
        };

    /// <summary>
    /// Stage keys accepted by <c>--model</c>: the subset whose alias is actually carried
    /// by engine model preferences — <see cref="StageNames.SpeechEnhancement"/> has no
    /// model-alias mapping and is deliberately rejected rather than silently ignored.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ModelPinStageNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [StageNames.Vad] = StageNames.Vad,
            [StageNames.Asr] = StageNames.Asr,
            [StageNames.Diarization] = StageNames.Diarization,
            [StageNames.Separation] = StageNames.Separation,
            [StageNames.OverlapRescue] = StageNames.OverlapRescue,
            [StageNames.Translation] = StageNames.Translation,
            [StageNames.Tts] = StageNames.Tts,
            [StageNames.TextRefinementAsr] = StageNames.TextRefinementAsr,
            [StageNames.LipSync] = StageNames.LipSync,
            [StageNames.LipSynthesis] = StageNames.LipSynthesis,
        };

    /// <summary>
    /// Parses one <c>stage=value</c> pin into <paramref name="pins"/>. Rejects missing or
    /// blank stage/value around the first '=', stages with no runtime inference mapping,
    /// duplicate stage keys, and (for <c>--provider</c>) unknown provider labels.
    /// </summary>
    private static bool TryParseStagePin(
        string argument,
        string flag,
        TextWriter error,
        Dictionary<string, string> pins)
    {
        int separator = argument.IndexOf('=');
        if (separator <= 0)
        {
            error.WriteLine($"Error: {flag} expects <stage>=<value>; got '{argument}'.");
            return false;
        }

        string stage = argument[..separator].Trim();
        string pinned = argument[(separator + 1)..].Trim();
        if (stage.Length == 0 || pinned.Length == 0)
        {
            error.WriteLine($"Error: {flag} expects non-blank stage and value around '='; got '{argument}'.");
            return false;
        }

        bool isProvider = string.Equals(flag, "--provider", StringComparison.Ordinal);
        IReadOnlyDictionary<string, string> stageNames =
            isProvider ? ProviderPinStageNames : ModelPinStageNames;
        if (!stageNames.TryGetValue(stage, out string? canonicalStage))
        {
            error.WriteLine(
                $"Error: {flag} stage '{stage}' does not accept a pin. " +
                $"Accepted stages: {string.Join(", ", stageNames.Values)}.");
            return false;
        }

        string stored = pinned;
        if (isProvider)
        {
            if (!ExecutionProviderTokens.TryParse(pinned, out ExecutionProviderKind providerKind))
            {
                error.WriteLine(
                    $"Error: {flag} value '{pinned}' is not a valid execution provider for stage '{canonicalStage}'. " +
                    $"Expected one of: {ExecutionProviderTokens.FormatSupportedCliTags()} (except auto).");
                return false;
            }

            stored = ExecutionProviderTokens.ToCanonicalTag(providerKind);
        }

        if (!pins.TryAdd(canonicalStage, stored))
        {
            error.WriteLine($"Error: duplicate {flag} pin for stage '{canonicalStage}'.");
            return false;
        }

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
