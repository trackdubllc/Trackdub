using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Media.Process;

namespace Trackdub.Media.Loudness;

public sealed class FfmpegLoudnessNormalizer : ILoudnessNormalizer
{
    private readonly IProcessRunner processRunner;
    private readonly FfmpegToolResolver toolResolver;

    public FfmpegLoudnessNormalizer(string? ffmpegPath = null)
        : this(new ProcessRunner(), ffmpegPath)
    {
    }

    internal FfmpegLoudnessNormalizer(IProcessRunner processRunner, string? ffmpegPath = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        toolResolver = new FfmpegToolResolver(ffmpegPath);
    }

    public async Task<LoudnessNormalizationResult> NormalizeAsync(
        LoudnessNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string inputPath = Path.GetFullPath(request.InputPath);
        string outputPath = Path.GetFullPath(request.OutputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input audio file was not found.", inputPath);
        }

        if (FilePathComparison.AreSame(inputPath, outputPath))
        {
            throw new InvalidOperationException("Loudness normalization output path must be different from the input audio path.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        string ffmpegPath = toolResolver.ResolveFfmpegPath();
        double targetLufs = FfmpegLoudnessCommandBuilder.NormalizeTargetLufs(request.TargetLufs);
        ProcessResult firstPass = await processRunner
            .RunAsync(
                ffmpegPath,
                FfmpegLoudnessCommandBuilder.BuildFirstPassArguments(inputPath, targetLufs),
                cancellationToken)
            .ConfigureAwait(false);
        if (firstPass.ExitCode != 0)
        {
            throw new InvalidOperationException(FfmpegErrorFormatter.BuildFailureMessage(
                "ffmpeg loudness first pass",
                firstPass.ExitCode,
                firstPass.StandardError));
        }

        LoudnormStats stats = FfmpegLoudnessCommandBuilder.ParseStats(firstPass.StandardError);
        ProcessResult secondPass = await processRunner
            .RunAsync(
                ffmpegPath,
                FfmpegLoudnessCommandBuilder.BuildSecondPassArguments(inputPath, outputPath, targetLufs, stats),
                cancellationToken)
            .ConfigureAwait(false);
        if (secondPass.ExitCode != 0)
        {
            throw new InvalidOperationException(FfmpegErrorFormatter.BuildFailureMessage(
                "ffmpeg loudness second pass",
                secondPass.ExitCode,
                secondPass.StandardError));
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("ffmpeg completed without producing normalized audio.");
        }

        LoudnormStats? outputStats = FfmpegLoudnessCommandBuilder.TryParseStats(secondPass.StandardError);
        return new LoudnessNormalizationResult(
            outputPath,
            targetLufs,
            outputStats?.OutputIntegratedLufs ?? stats.OutputIntegratedLufs,
            Warnings: []);
    }

    public async Task<LoudnessAnalysisResult> AnalyzeAsync(
        LoudnessAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string inputPath = Path.GetFullPath(request.InputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input audio file was not found.", inputPath);
        }

        string ffmpegPath = toolResolver.ResolveFfmpegPath();
        ProcessResult firstPass = await processRunner
            .RunAsync(
                ffmpegPath,
                FfmpegLoudnessCommandBuilder.BuildFirstPassArguments(inputPath, ExportLoudnessTargets.OnlineLufs),
                cancellationToken)
            .ConfigureAwait(false);
        if (firstPass.ExitCode != 0)
        {
            throw new InvalidOperationException(FfmpegErrorFormatter.BuildFailureMessage(
                "ffmpeg loudness analysis",
                firstPass.ExitCode,
                firstPass.StandardError));
        }

        LoudnormStats stats = FfmpegLoudnessCommandBuilder.ParseStats(firstPass.StandardError);
        return new LoudnessAnalysisResult(inputPath, stats.InputIntegratedLufs, Warnings: []);
    }
}

internal static class FfmpegLoudnessCommandBuilder
{
    private const double TruePeak = -1.5d;
    private const double LoudnessRange = 11d;

    public static IReadOnlyList<string> BuildFirstPassArguments(string inputPath, double targetLufs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        double normalizedTarget = NormalizeTargetLufs(targetLufs);
        return
        [
            "-y",
            "-hide_banner",
            "-nostats",
            "-loglevel",
            "info",
            "-i",
            inputPath,
            "-af",
            BuildFirstPassFilter(normalizedTarget),
            "-f",
            "null",
            "-"
        ];
    }

    public static IReadOnlyList<string> BuildSecondPassArguments(
        string inputPath,
        string outputPath,
        double targetLufs,
        LoudnormStats stats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(stats);
        double normalizedTarget = NormalizeTargetLufs(targetLufs);
        return
        [
            "-y",
            "-hide_banner",
            "-nostats",
            "-loglevel",
            "info",
            "-i",
            inputPath,
            "-af",
            BuildSecondPassFilter(normalizedTarget, stats),
            "-ar",
            "48000",
            "-c:a",
            "pcm_s16le",
            outputPath
        ];
    }

    public static LoudnormStats ParseStats(string standardError) =>
        TryParseStats(standardError)
        ?? throw new InvalidOperationException("ffmpeg loudnorm did not return parseable JSON stats.");

    public static LoudnormStats? TryParseStats(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return null;
        }

        foreach (string json in EnumerateJsonObjects(standardError))
        {
            try
            {
                LoudnormStatsPayload? payload = JsonSerializer.Deserialize<LoudnormStatsPayload>(json);
                if (payload is null)
                {
                    continue;
                }

                return new LoudnormStats(
                    ParseRequired(payload.InputIntegratedLufs, "input_i"),
                    ParseRequired(payload.InputTruePeak, "input_tp"),
                    ParseRequired(payload.InputLra, "input_lra"),
                    ParseRequired(payload.InputThreshold, "input_thresh"),
                    ParseOptional(payload.OutputIntegratedLufs),
                    ParseRequired(payload.TargetOffset, "target_offset"));
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return null;
    }

    public static double NormalizeTargetLufs(double targetLufs) =>
        ExportLoudnessTargets.NormalizeTargetLufs(targetLufs);

    internal static string BuildFirstPassFilter(double targetLufs) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"loudnorm=I={NormalizeTargetLufs(targetLufs):0.##}:TP={TruePeak:0.##}:LRA={LoudnessRange:0.##}:print_format=json");

    internal static string BuildSecondPassFilter(double targetLufs, LoudnormStats stats) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"loudnorm=I={NormalizeTargetLufs(targetLufs):0.##}:TP={TruePeak:0.##}:LRA={LoudnessRange:0.##}:measured_I={FormatFilterNumber(stats.InputIntegratedLufs, NormalizeTargetLufs(targetLufs), -99d, 0d)}:measured_TP={FormatFilterNumber(stats.InputTruePeak, TruePeak, -99d, 99d)}:measured_LRA={FormatFilterNumber(stats.InputLra, 0d, 0d, 99d)}:measured_thresh={FormatFilterNumber(stats.InputThreshold, -70d, -99d, 0d)}:offset={FormatFilterNumber(stats.TargetOffset, 0d, -99d, 99d)}:linear=true:print_format=json");

    private static double ParseRequired(string? value, string propertyName) =>
        ParseOptional(value)
        ?? throw new InvalidOperationException($"ffmpeg loudnorm JSON is missing '{propertyName}'.");

    private static double? ParseOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (string.Equals(normalized, "-inf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "-infinity", StringComparison.OrdinalIgnoreCase))
        {
            return double.NegativeInfinity;
        }

        if (string.Equals(normalized, "inf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "+inf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "infinity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "+infinity", StringComparison.OrdinalIgnoreCase))
        {
            return double.PositiveInfinity;
        }

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    private static string FormatFilterNumber(double value, double fallback, double minimum, double maximum)
    {
        double finiteValue = double.IsFinite(value) ? value : fallback;
        return Math.Clamp(finiteValue, minimum, maximum).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> EnumerateJsonObjects(string value)
    {
        int start = -1;
        int depth = 0;
        bool isInString = false;
        bool isEscaped = false;

        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (isInString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (current == '\\')
                {
                    isEscaped = true;
                }
                else if (current == '"')
                {
                    isInString = false;
                }

                continue;
            }

            if (current == '"')
            {
                isInString = true;
                continue;
            }

            if (current == '{')
            {
                if (depth == 0)
                {
                    start = index;
                }

                depth++;
                continue;
            }

            if (current == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return value[start..(index + 1)];
                    start = -1;
                }
            }
        }
    }

    private sealed record LoudnormStatsPayload(
        [property: JsonPropertyName("input_i")] string? InputIntegratedLufs,
        [property: JsonPropertyName("input_tp")] string? InputTruePeak,
        [property: JsonPropertyName("input_lra")] string? InputLra,
        [property: JsonPropertyName("input_thresh")] string? InputThreshold,
        [property: JsonPropertyName("output_i")] string? OutputIntegratedLufs,
        [property: JsonPropertyName("target_offset")] string? TargetOffset);
}

internal sealed record LoudnormStats(
    double InputIntegratedLufs,
    double InputTruePeak,
    double InputLra,
    double InputThreshold,
    double? OutputIntegratedLufs,
    double TargetOffset);
