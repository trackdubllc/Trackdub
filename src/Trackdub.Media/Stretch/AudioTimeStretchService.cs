using Trackdub.Contracts;
using Trackdub.Domain.Tts;
using Trackdub.Media.Process;

namespace Trackdub.Media.Stretch;

public sealed class AudioTimeStretchService : IAudioTimeStretchService
{
    private static readonly ProcessRunOptions FilterDiscoveryProcessOptions = new(Timeout: TimeSpan.FromSeconds(15));

    private readonly IProcessRunner processRunner;
    private readonly FfmpegToolResolver toolResolver;
    private bool? rubberbandAvailable;

    public AudioTimeStretchService(string? ffmpegPath = null)
        : this(new ProcessRunner(), ffmpegPath)
    {
    }

    internal AudioTimeStretchService(IProcessRunner processRunner, string? ffmpegPath = null)
    {
        this.processRunner = processRunner;
        toolResolver = new FfmpegToolResolver(ffmpegPath);
    }

    public async Task<AudioTimeStretchResult> StretchAsync(
        AudioTimeStretchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        if (!File.Exists(request.InputPath))
        {
            throw new FileNotFoundException("Input audio file was not found.", request.InputPath);
        }

        string ffmpegPath = toolResolver.ResolveFfmpegPath();
        bool canUseRubberband = request.EnableRubberband &&
                                await IsRubberbandAvailableAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        TimeStretchFilterPlan plan = FfmpegTimeStretchCommandBuilder.BuildFilterPlan(
            request.TempoRatio,
            request.EnableRubberband,
            request.RubberbandThreshold,
            canUseRubberband);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
        ProcessResult result = await RunStretchAsync(ffmpegPath, request, plan.Filter, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0 && File.Exists(request.OutputPath))
        {
            return new AudioTimeStretchResult(plan.Engine, plan.UsedFallback, plan.Message);
        }

        if (plan.Engine is TtsStretchEngine.Rubberband)
        {
            TimeStretchFilterPlan fallbackPlan = FfmpegTimeStretchCommandBuilder.BuildFilterPlan(
                request.TempoRatio,
                enableRubberband: false,
                request.RubberbandThreshold,
                rubberbandAvailable: false);
            result = await RunStretchAsync(ffmpegPath, request, fallbackPlan.Filter, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0 && File.Exists(request.OutputPath))
            {
                return new AudioTimeStretchResult(
                    fallbackPlan.Engine,
                    UsedFallback: true,
                    "FFmpeg rubberband stretch failed; used atempo instead.");
            }
        }

        throw new InvalidOperationException(
            $"ffmpeg time stretch failed with exit code {result.ExitCode}: {result.StandardError}".Trim());
    }

    private Task<ProcessResult> RunStretchAsync(
        string ffmpegPath,
        AudioTimeStretchRequest request,
        string filter,
        CancellationToken cancellationToken)
    {
        if (File.Exists(request.OutputPath))
        {
            File.Delete(request.OutputPath);
        }

        IReadOnlyList<string> arguments = FfmpegTimeStretchCommandBuilder.BuildArguments(
            request.InputPath,
            request.OutputPath,
            filter);
        return processRunner.RunAsync(ffmpegPath, arguments, cancellationToken);
    }

    private async Task<bool> IsRubberbandAvailableAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        if (rubberbandAvailable is bool cached)
        {
            return cached;
        }

        ProcessResult result = await processRunner.RunAsync(
            ffmpegPath,
            ["-hide_banner", "-filters"],
            cancellationToken,
            FilterDiscoveryProcessOptions).ConfigureAwait(false);
        bool available = result.ExitCode == 0 &&
                         result.StandardOutput.Contains("rubberband", StringComparison.OrdinalIgnoreCase);
        rubberbandAvailable = available;
        return available;
    }
}
