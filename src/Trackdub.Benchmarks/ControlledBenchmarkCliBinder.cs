using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks;

/// <summary>
/// Outcome of offering one <c>--option value</c> pair to the shared binder.
/// </summary>
internal enum ControlledCliOptionResult
{
    /// <summary>The option belongs to a specific command; the caller handles it.</summary>
    NotHandled,

    /// <summary>The option was recognized and applied to the shared state.</summary>
    Applied,

    /// <summary>The option was recognized but its value was invalid; the error is written.</summary>
    Failed,
}

/// <summary>
/// Flags and options shared by the <c>controlled</c> and <c>controlled-matrix</c> commands.
/// Each command keeps only its divergent arms (<c>--stage</c> vs <c>--stages</c>,
/// <c>--model</c> shapes, <c>--report-dir</c>) in its own switch and delegates
/// everything else here, so the unpacking logic lives in exactly one place.
/// </summary>
internal sealed class ControlledBenchmarkCliOptions
{
    public string? OutputDirectory { get; set; }

    public string TargetLanguage { get; set; } = "es";

    public string? SourceLanguage { get; set; }

    public string Mode { get; set; } = "fresh-process";

    public bool ReuseEngineCache { get; set; }
    public bool TranslationStreaming { get; set; }

    public bool Mock { get; set; }

    public bool DryRun { get; set; }

    public string? ModelDirectory { get; set; }

    public string? Provider { get; set; }

    public string? FfmpegPath { get; set; }

    public string? FfprobePath { get; set; }

    public string? ExpectedFixtureSha256 { get; set; }

    public int RunCount { get; set; } = 1;

    public ResourceTelemetryBounds ResourceTelemetryBounds { get; set; } = new();
}

/// <summary>
/// Single binder for the CLI surface shared by <c>controlled</c> and
/// <c>controlled-matrix</c>. Leaf validation stays in
/// <see cref="ResourceTelemetryOptionsParser"/>; this binder owns the switch-level
/// unpacking (flags, value reads, run count, shared string options) that used to be
/// duplicated across both entry points.
/// </summary>
internal static class ControlledBenchmarkCliBinder
{
    /// <summary>
    /// Applies a valueless flag. Returns true when the argument was consumed.
    /// </summary>
    public static bool TryApplyFlag(string arg, ControlledBenchmarkCliOptions state)
    {
        ArgumentNullException.ThrowIfNull(arg);
        ArgumentNullException.ThrowIfNull(state);
        switch (arg)
        {
            case "--reuse-engine-cache":
                state.ReuseEngineCache = true;
                return true;
            case "--translation-streaming":
                state.TranslationStreaming = true;
                return true;
            case "--mock":
                state.Mock = true;
                return true;
            case "--dry-run":
                state.DryRun = true;
                state.Mock = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads the value following <c>args[index]</c> (the option name) and advances the index.
    /// Writes <c>Missing value for {option}.</c> and returns false when no value follows, when the
    /// next token looks like another option, or when the token is empty or quote-only — a value
    /// the option grammar cannot use, which must not be swallowed as this option's value (the
    /// caller would otherwise silently configure the wrong path or mode).
    /// </summary>
    public static bool TryReadValue(string[] args, ref int index, TextWriter error, out string value)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(error);
        if (index + 1 >= args.Length || IsUnusableValueToken(args[index + 1]))
        {
            error.WriteLine($"Missing value for {args[index]}.");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    /// <summary>
    /// Whether a token cannot serve as an option's value: another long option (the option's value
    /// was simply omitted), or a token that is empty/whitespace or consists only of unpaired
    /// quotes left behind by the shell.
    /// </summary>
    private static bool IsUnusableValueToken(string token)
    {
        string trimmed = token.Trim();
        return trimmed.Length == 0
            || trimmed.StartsWith("--", StringComparison.Ordinal)
            || trimmed is "\"" or "'" or "\"\"" or "''";
    }

    /// <summary>
    /// Applies one shared <c>--option value</c> pair. Returns <see cref="ControlledCliOptionResult.NotHandled"/>
    /// for command-specific options (<c>--stage(s)</c>, <c>--model</c>, <c>--report-dir</c>) and unknowns.
    /// </summary>
    public static ControlledCliOptionResult TryApplyOption(
        string option,
        string value,
        ControlledBenchmarkCliOptions state,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(error);
        switch (option)
        {
            case "--output": state.OutputDirectory = value; return ControlledCliOptionResult.Applied;
            case "--language": state.TargetLanguage = value; return ControlledCliOptionResult.Applied;
            case "--source-language": state.SourceLanguage = value; return ControlledCliOptionResult.Applied;
            case "--mode": state.Mode = value; return ControlledCliOptionResult.Applied;
            case "--model-directory": state.ModelDirectory = value; return ControlledCliOptionResult.Applied;
            case "--provider": state.Provider = value; return ControlledCliOptionResult.Applied;
            case "--ffmpeg": state.FfmpegPath = value; return ControlledCliOptionResult.Applied;
            case "--ffprobe": state.FfprobePath = value; return ControlledCliOptionResult.Applied;
            case "--sha256": state.ExpectedFixtureSha256 = value; return ControlledCliOptionResult.Applied;
            case "--runs":
                if (!TryParseRunCount(value, error, out int runCount))
                {
                    return ControlledCliOptionResult.Failed;
                }

                state.RunCount = runCount;
                return ControlledCliOptionResult.Applied;
            case var opt when ResourceTelemetryOptionsParser.IsResourceOption(opt):
                if (!ResourceTelemetryOptionsParser.TryApply(
                    opt, value, state.ResourceTelemetryBounds, error, out ResourceTelemetryBounds bounds))
                {
                    return ControlledCliOptionResult.Failed;
                }

                state.ResourceTelemetryBounds = bounds;
                return ControlledCliOptionResult.Applied;
            default:
                return ControlledCliOptionResult.NotHandled;
        }
    }

    public static bool TryParseRunCount(string value, TextWriter error, out int runCount)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(error);
        if (!int.TryParse(value, out runCount) || runCount <= 0)
        {
            error.WriteLine($"Invalid run count '{value}'. Expected a positive integer.");
            return false;
        }

        return true;
    }
}
