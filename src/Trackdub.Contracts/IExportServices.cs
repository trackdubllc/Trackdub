namespace Trackdub.Contracts;

public enum ExportOutputContainer
{
    Mp4 = 0,
    Mkv = 1
}

public interface IExportRenderer
{
    Task<ExportRenderResult> RenderAsync(
        ExportPlan plan,
        CancellationToken cancellationToken);
}

public sealed record ExportPlan(
    string SourceMediaPath,
    string DubbedAudioPath,
    string OutputPath,
    ExportOutputContainer Container,
    string? BurnInSubtitlePath,
    string? SourceLanguage,
    string? TargetLanguage,
    VideoEncoderPreference VideoEncoder = VideoEncoderPreference.Auto,
    bool RequiresWatermark = false,
    int OutputHeight = 0);

public sealed record ExportRenderResult(
    string OutputPath,
    IReadOnlyList<string> Warnings);

public interface ILoudnessNormalizer
{
    Task<LoudnessAnalysisResult> AnalyzeAsync(
        LoudnessAnalysisRequest request,
        CancellationToken cancellationToken);

    Task<LoudnessNormalizationResult> NormalizeAsync(
        LoudnessNormalizationRequest request,
        CancellationToken cancellationToken);
}

public sealed record LoudnessAnalysisRequest(
    string InputPath);

public sealed record LoudnessAnalysisResult(
    string InputPath,
    double IntegratedLufs,
    IReadOnlyList<string> Warnings);

public sealed record LoudnessNormalizationRequest(
    string InputPath,
    string OutputPath,
    double TargetLufs);

public sealed record LoudnessNormalizationResult(
    string OutputPath,
    double TargetLufs,
    double? AchievedLufs,
    IReadOnlyList<string> Warnings);

public static class ExportLoudnessTargets
{
    public const double OnlineLufs = -14d;
    public const double BroadcastLufs = -23d;

    public static double NormalizeTargetLufs(double targetLufs) =>
        double.IsFinite(targetLufs)
            ? Math.Clamp(targetLufs, -70d, -5d)
            : OnlineLufs;
}

public interface IExportToolAvailabilityService
{
    ExportToolAvailability CheckAvailability();
}

public sealed record ExportToolAvailability(
    bool IsAvailable,
    string? Message,
    string? FfmpegPath,
    string? FfprobePath)
{
    public static ExportToolAvailability Available(string ffmpegPath, string ffprobePath) =>
        new(true, null, ffmpegPath, ffprobePath);

    public static ExportToolAvailability Unavailable(string message) =>
        new(false, message, null, null);
}
