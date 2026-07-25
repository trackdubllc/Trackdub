using Trackdub.Contracts;
using Trackdub.Media.Process;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Enhancement;

public sealed class FfmpegSpeechAudioProcessingService : ISpeechAudioProcessingService
{
    private readonly IProcessRunner processRunner;
    private readonly FfmpegToolResolver toolResolver;

    public FfmpegSpeechAudioProcessingService(string? ffmpegPath = null)
        : this(new ProcessRunner(), ffmpegPath)
    {
    }

    internal FfmpegSpeechAudioProcessingService(IProcessRunner processRunner, string? ffmpegPath = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        toolResolver = new FfmpegToolResolver(ffmpegPath);
    }

    public async Task<SpeechAudioProcessingResult> ProcessAsync(
        SpeechAudioProcessingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilterSelection.FilterChain);

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
            FfmpegSpeechAudioEnhancementCommandBuilder.BuildArguments(
                fullSourcePath,
                fullDestinationPath,
                request.FilterSelection.FilterChain),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg speech audio processing failed with exit code {result.ExitCode}: {result.StandardError}".Trim());
        }

        if (!File.Exists(fullDestinationPath))
        {
            throw new InvalidOperationException("ffmpeg completed without producing processed speech audio.");
        }

        WavePcm16Info waveInfo = await WavePcm16.ReadInfoAsync(fullDestinationPath, cancellationToken).ConfigureAwait(false);
        return new SpeechAudioProcessingResult(
            fullDestinationPath,
            waveInfo.DurationSeconds,
            waveInfo.SampleRate,
            waveInfo.ChannelCount,
            waveInfo.SampleFrames,
            request.FilterSelection);
    }
}
