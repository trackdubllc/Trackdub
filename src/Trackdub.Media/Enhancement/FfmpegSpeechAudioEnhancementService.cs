using Trackdub.Contracts;
using Trackdub.Media.Process;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Enhancement;

public sealed class FfmpegSpeechAudioEnhancementService : ISpeechAudioEnhancementService
{
    private readonly IProcessRunner processRunner;
    private readonly FfmpegToolResolver toolResolver;

    public FfmpegSpeechAudioEnhancementService(string? ffmpegPath = null)
        : this(new ProcessRunner(), ffmpegPath)
    {
    }

    internal FfmpegSpeechAudioEnhancementService(IProcessRunner processRunner, string? ffmpegPath = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        toolResolver = new FfmpegToolResolver(ffmpegPath);
    }

    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);

        string fullSourcePath = Path.GetFullPath(request.SourceAudioPath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("Source speech audio file was not found.", fullSourcePath);
        }

        string fullDestinationPath = Path.GetFullPath(request.DestinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationPath)!);
        if (File.Exists(fullDestinationPath))
        {
            File.Delete(fullDestinationPath);
        }

        ProcessResult result = await processRunner.RunAsync(
            toolResolver.ResolveFfmpegPath(),
            FfmpegSpeechAudioEnhancementCommandBuilder.BuildArguments(fullSourcePath, fullDestinationPath),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg speech enhancement failed with exit code {result.ExitCode}: {result.StandardError}".Trim());
        }

        if (!File.Exists(fullDestinationPath))
        {
            throw new InvalidOperationException("ffmpeg completed without producing enhanced speech audio.");
        }

        WavePcm16Info waveInfo = await WavePcm16.ReadInfoAsync(fullDestinationPath, cancellationToken).ConfigureAwait(false);
        return new SpeechAudioEnhancementResult(
            fullDestinationPath,
            waveInfo.DurationSeconds,
            waveInfo.SampleRate,
            waveInfo.ChannelCount,
            waveInfo.SampleFrames);
    }
}
