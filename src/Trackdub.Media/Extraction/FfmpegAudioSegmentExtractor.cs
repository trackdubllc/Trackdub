using System.Globalization;
using Trackdub.Contracts.Pipeline;
using Trackdub.Media.Process;

namespace Trackdub.Media.Extraction;

public sealed class FfmpegAudioSegmentExtractor : IAudioSegmentExtractor
{
    private readonly IProcessRunner _processRunner;
    private readonly FfmpegToolResolver _toolResolver;

    public FfmpegAudioSegmentExtractor(string? ffmpegPath = null)
        : this(new ProcessRunner(), ffmpegPath)
    {
    }

    internal FfmpegAudioSegmentExtractor(IProcessRunner processRunner, string? ffmpegPath = null)
    {
        _processRunner = processRunner;
        _toolResolver = new FfmpegToolResolver(ffmpegPath);
    }

    public async Task<string> ExtractSegmentAsync(
        string sourceAudioPath,
        TimeSpan start,
        TimeSpan end,
        string outputWavPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputWavPath);

        string? outDir = Path.GetDirectoryName(outputWavPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        string ffmpegPath = _toolResolver.ResolveFfmpegPath();
        string startSec = start.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture);
        string endSec = end.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture);

        var args = new List<string>
        {
            "-ss", startSec,
            "-to", endSec,
            "-i", sourceAudioPath,
            "-ar", "16000",
            "-ac", "1",
            "-f", "wav",
            "-y",
            outputWavPath
        };

        ProcessResult result = await _processRunner.RunAsync(ffmpegPath, args, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg audio segment extraction failed (exit {result.ExitCode}): {result.StandardError}");
        }

        return outputWavPath;
    }
}
