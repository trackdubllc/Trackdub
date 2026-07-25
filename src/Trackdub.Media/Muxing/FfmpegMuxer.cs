using Trackdub.Contracts;
using Trackdub.Media.Process;

namespace Trackdub.Media.Muxing;

public sealed class FfmpegMuxer : IExportRenderer
{
    private static readonly TimeSpan ExportMuxTimeout = TimeSpan.FromHours(6);
    private readonly IProcessRunner processRunner;
    private readonly FfmpegToolResolver toolResolver;
    private readonly IFfmpegVideoEncoderCapabilities capabilities;
    private readonly IMediaGpuHintProvider? gpuHintProvider;

    public FfmpegMuxer(string? ffmpegPath = null)
        : this(new FfmpegVideoEncoderCapabilityService(ffmpegPath), gpuHintProvider: null, ffmpegPath)
    {
    }

    public FfmpegMuxer(IFfmpegVideoEncoderCapabilities capabilities, string? ffmpegPath = null)
        : this(capabilities, gpuHintProvider: null, ffmpegPath)
    {
    }

    public FfmpegMuxer(
        IFfmpegVideoEncoderCapabilities capabilities,
        IMediaGpuHintProvider? gpuHintProvider,
        string? ffmpegPath = null)
        : this(new ProcessRunner(), capabilities, gpuHintProvider, ffmpegPath)
    {
    }

    internal FfmpegMuxer(IProcessRunner processRunner, string? ffmpegPath = null)
        : this(processRunner, new FfmpegVideoEncoderCapabilityService(ffmpegPath), gpuHintProvider: null, ffmpegPath)
    {
    }

    internal FfmpegMuxer(
        IProcessRunner processRunner,
        IFfmpegVideoEncoderCapabilities capabilities,
        IMediaGpuHintProvider? gpuHintProvider,
        string? ffmpegPath = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        this.gpuHintProvider = gpuHintProvider;
        toolResolver = new FfmpegToolResolver(ffmpegPath);
    }

    public async Task<ExportRenderResult> RenderAsync(
        ExportPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string sourceMediaPath = Path.GetFullPath(plan.SourceMediaPath);
        string dubbedAudioPath = Path.GetFullPath(plan.DubbedAudioPath);
        string outputPath = Path.GetFullPath(plan.OutputPath);
        if (!File.Exists(sourceMediaPath))
        {
            throw new FileNotFoundException("Source video file was not found.", sourceMediaPath);
        }

        if (!File.Exists(dubbedAudioPath))
        {
            throw new FileNotFoundException("Dubbed audio file was not found.", dubbedAudioPath);
        }

        if (FilePathComparison.AreSame(outputPath, sourceMediaPath) ||
            FilePathComparison.AreSame(outputPath, dubbedAudioPath))
        {
            throw new InvalidOperationException("Export output path must be different from the source media and dubbed audio paths.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        string ffmpegPath = toolResolver.ResolveFfmpegPath();

        var warnings = new List<string>();
        VideoEncodeProfile? encodeProfile = null;
        FfmpegVideoEncoderSnapshot? encoderSnapshot = null;
        bool requiresVideoEncode = !string.IsNullOrWhiteSpace(plan.BurnInSubtitlePath) || plan.RequiresWatermark;
        if (requiresVideoEncode)
        {
            if (plan.VideoEncoder == VideoEncoderPreference.Software)
            {
                encoderSnapshot = FfmpegVideoEncoderSnapshot.Empty;
                encodeProfile = FfmpegVideoEncoderSelector.Resolve(
                    VideoEncoderPreference.Software,
                    encoderSnapshot,
                    new MediaGpuHint(HasGpu: false));
            }
            else
            {
                encoderSnapshot = await capabilities
                    .RefreshAsync(cancellationToken)
                    .ConfigureAwait(false);
                MediaGpuHint gpuHint = gpuHintProvider is null
                    ? new MediaGpuHint(HasGpu: false)
                    : await gpuHintProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                encodeProfile = FfmpegVideoEncoderSelector.Resolve(plan.VideoEncoder, encoderSnapshot, gpuHint);
            }

            if (!string.IsNullOrWhiteSpace(encodeProfile.Message))
            {
                warnings.Add(encodeProfile.Message);
            }
        }
        ProcessResult result = await RunMuxAsync(ffmpegPath, plan, encodeProfile, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 &&
            encodeProfile is not null &&
            !string.Equals(encodeProfile.EncoderName, "libx264", StringComparison.OrdinalIgnoreCase))
        {
            VideoEncodeProfile softwareProfile = FfmpegVideoEncoderSelector.Resolve(
                VideoEncoderPreference.Software,
                encoderSnapshot ?? capabilities.GetSnapshot());
            warnings.Add(
                $"Hardware video encoder '{encodeProfile.EncoderName}' failed; retrying with software libx264.");
            result = await RunMuxAsync(ffmpegPath, plan, softwareProfile, cancellationToken).ConfigureAwait(false);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(FfmpegErrorFormatter.BuildFailureMessage(
                "ffmpeg export mux",
                result.ExitCode,
                result.StandardError));
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("ffmpeg completed without producing an export video.");
        }

        return new ExportRenderResult(outputPath, warnings);
    }

    private Task<ProcessResult> RunMuxAsync(
        string ffmpegPath,
        ExportPlan plan,
        VideoEncodeProfile? encodeProfile,
        CancellationToken cancellationToken) =>
        processRunner.RunAsync(
            ffmpegPath,
            FfmpegMuxCommandBuilder.BuildArguments(plan, encodeProfile),
            cancellationToken,
            new ProcessRunOptions(Timeout: ExportMuxTimeout));

}

internal static class FfmpegMuxCommandBuilder
{
    public static IReadOnlyList<string> BuildArguments(ExportPlan plan, VideoEncodeProfile? encodeProfile = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.SourceMediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.DubbedAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.OutputPath);

        var arguments = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error"
        };

        if (encodeProfile?.InputHwaccelArguments is { Count: > 0 } hwaccelArguments)
        {
            arguments.AddRange(hwaccelArguments);
        }

        arguments.AddRange(
        [
            "-i",
            Path.GetFullPath(plan.SourceMediaPath),
            "-i",
            Path.GetFullPath(plan.DubbedAudioPath),
            "-map",
            "0:v:0",
            "-map",
            "1:a:0"
        ]);

        if (string.IsNullOrWhiteSpace(plan.BurnInSubtitlePath) && !plan.RequiresWatermark)
        {
            arguments.AddRange(["-c:v", "copy"]);
        }
        else
        {
            VideoEncodeProfile profile = encodeProfile ??
                                         FfmpegVideoEncoderSelector.Resolve(
                                             VideoEncoderPreference.Software,
                                             FfmpegVideoEncoderSnapshot.Empty);

            arguments.Add("-vf");
            arguments.Add(BuildVideoFilterChain(plan.BurnInSubtitlePath, plan.RequiresWatermark, plan.OutputHeight, profile.VideoFilterPrefix));
            arguments.Add("-c:v");
            arguments.Add(profile.EncoderName);
            arguments.AddRange(profile.EncoderArguments);
        }

        arguments.AddRange(BuildAudioArguments(plan.Container));
        arguments.Add("-shortest");
        arguments.AddRange(BuildMetadataArguments(plan.SourceLanguage, plan.TargetLanguage));

        if (plan.Container is ExportOutputContainer.Mp4)
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        arguments.Add(Path.GetFullPath(plan.OutputPath));
        return arguments;
    }

    internal static string BuildVideoFilterChain(string? subtitlePath, bool requiresWatermark, int outputHeight, string? filterPrefix = null)
    {
        var filters = new List<string>();

        if (!string.IsNullOrWhiteSpace(subtitlePath))
        {
            string escaped = EscapeSubtitlePath(Path.GetFullPath(subtitlePath));
            filters.Add($"subtitles='{escaped}'");
        }

        if (requiresWatermark)
        {
            filters.Add(BuildWatermarkFilter(outputHeight));
        }

        string combined = string.Join(",", filters);
        return string.IsNullOrWhiteSpace(filterPrefix)
            ? combined
            : $"{filterPrefix}{combined}";
    }

    internal static string BuildSubtitleFilter(string subtitlePath, string? filterPrefix = null)
    {
        string escaped = EscapeSubtitlePath(Path.GetFullPath(subtitlePath));
        string filter = $"subtitles='{escaped}'";
        return string.IsNullOrWhiteSpace(filterPrefix)
            ? filter
            : $"{filterPrefix}{filter}";
    }

    internal static string BuildWatermarkFilter(int outputHeight)
    {
        int fontSize = CalculateWatermarkFontSize(outputHeight);
        return $"drawtext=text='Made with Trackdub':fontsize={fontSize}:fontcolor=white@0.4:x=w-tw-20:y=h-th-20";
    }

    internal static int CalculateWatermarkFontSize(int outputHeight)
    {
        if (outputHeight <= 0)
        {
            return 24; // Default for unknown resolution (assumes ~1080p)
        }

        return Math.Max(16, outputHeight / 45);
    }

    private static IReadOnlyList<string> BuildAudioArguments(ExportOutputContainer container) =>
        container is ExportOutputContainer.Mkv
            ? ["-c:a", "libopus", "-b:a", "160k"]
            : ["-c:a", "aac", "-b:a", "192k"];

    private static IReadOnlyList<string> BuildMetadataArguments(string? sourceLanguage, string? targetLanguage)
    {
        var arguments = new List<string>
        {
            "-metadata",
            "DUBBED_BY=Trackdub"
        };

        if (!string.IsNullOrWhiteSpace(sourceLanguage))
        {
            arguments.AddRange(["-metadata", $"source_language={sourceLanguage.Trim().ToLowerInvariant()}"]);
        }

        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            arguments.AddRange(["-metadata", $"target_language={targetLanguage.Trim().ToLowerInvariant()}"]);
        }

        return arguments;
    }

    private static string EscapeSubtitlePath(string path) =>
        path
            .Replace('\\', '/')
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", @"\'", StringComparison.Ordinal)
            .Replace(",", @"\,", StringComparison.Ordinal)
            .Replace(";", @"\;", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal)
            .Replace("]", @"\]", StringComparison.Ordinal);

}
