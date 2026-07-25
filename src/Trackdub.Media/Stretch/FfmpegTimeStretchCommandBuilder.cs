using System.Globalization;
using Trackdub.Domain.Tts;

namespace Trackdub.Media.Stretch;

internal sealed record TimeStretchFilterPlan(
    string Filter,
    TtsStretchEngine Engine,
    bool UsedFallback,
    string? Message);

internal static class FfmpegTimeStretchCommandBuilder
{
    public static TimeStretchFilterPlan BuildFilterPlan(
        double tempoRatio,
        bool enableRubberband,
        double rubberbandThreshold,
        bool rubberbandAvailable)
    {
        ValidateTempoRatio(tempoRatio);

        if (enableRubberband &&
            Math.Abs(tempoRatio - 1d) >= NormalizeThreshold(rubberbandThreshold) &&
            rubberbandAvailable)
        {
            return new TimeStretchFilterPlan(
                $"rubberband=tempo={FormatRatio(tempoRatio)}",
                TtsStretchEngine.Rubberband,
                UsedFallback: false,
                Message: null);
        }

        TimeStretchFilterPlan atempoPlan = BuildAtempoFilterPlan(tempoRatio);
        if (enableRubberband &&
            Math.Abs(tempoRatio - 1d) >= NormalizeThreshold(rubberbandThreshold) &&
            !rubberbandAvailable)
        {
            return atempoPlan with
            {
                UsedFallback = true,
                Message = "FFmpeg rubberband filter is unavailable; used atempo instead."
            };
        }

        return atempoPlan;
    }

    public static IReadOnlyList<string> BuildArguments(
        string inputPath,
        string outputPath,
        string audioFilter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilter);

        return
        [
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            inputPath,
            "-vn",
            "-filter:a",
            audioFilter,
            "-c:a",
            "pcm_s16le",
            outputPath
        ];
    }

    private static TimeStretchFilterPlan BuildAtempoFilterPlan(double tempoRatio)
    {
        if (tempoRatio is >= 0.5d and <= 2.0d)
        {
            return new TimeStretchFilterPlan(
                $"atempo={FormatRatio(tempoRatio)}",
                TtsStretchEngine.Atempo,
                UsedFallback: false,
                Message: null);
        }

        if (tempoRatio is >= 0.25d and <= 4.0d)
        {
            double chainedRatio = Math.Sqrt(tempoRatio);
            return new TimeStretchFilterPlan(
                $"atempo={FormatRatio(chainedRatio)},atempo={FormatRatio(chainedRatio)}",
                TtsStretchEngine.Atempo,
                UsedFallback: false,
                Message: null);
        }

        throw new ArgumentOutOfRangeException(nameof(tempoRatio), "Tempo ratio must be between 0.25x and 4.0x.");
    }

    private static void ValidateTempoRatio(double tempoRatio)
    {
        if (!double.IsFinite(tempoRatio) || tempoRatio <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoRatio), "Tempo ratio must be positive.");
        }
    }

    private static double NormalizeThreshold(double threshold) =>
        double.IsFinite(threshold) && threshold >= 0d
            ? threshold
            : 0.15d;

    private static string FormatRatio(double ratio) =>
        ratio.ToString("0.############", CultureInfo.InvariantCulture);
}
