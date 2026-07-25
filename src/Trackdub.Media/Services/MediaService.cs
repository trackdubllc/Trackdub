namespace Trackdub.Media.Services;

using Microsoft.Extensions.Logging;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Stub implementation of IMediaService.
/// </summary>
public class MediaService(ILogger<MediaService> logger) : IMediaService
{
    public async Task<string> ExtractAudioAsync(
        string inputPath,
        string outputFormat = "wav",
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Extracting audio from {InputPath} to {OutputFormat}", inputPath, outputFormat);

        var outputPath = Path.Combine(
            Path.GetDirectoryName(inputPath) ?? ".",
            $"{Path.GetFileNameWithoutExtension(inputPath)}_audio.{outputFormat}");

        await File.WriteAllBytesAsync(outputPath, new byte[] { 0 }, cancellationToken);

        return outputPath;
    }

    public async Task<string> ExtractVideoAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Extracting video from {InputPath}", inputPath);

        var outputPath = Path.Combine(
            Path.GetDirectoryName(inputPath) ?? ".",
            $"{Path.GetFileNameWithoutExtension(inputPath)}_video.mp4");

        await File.WriteAllBytesAsync(outputPath, new byte[] { 0 }, cancellationToken);

        return outputPath;
    }

    public async Task<byte[]> MixAudioAsync(
        byte[] dubbedAudio,
        byte[] originalAudio,
        bool keepOriginal = true,
        bool normalizeLevel = true,
        float targetLUFS = -16f,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Mixing audio: dubbed={DubbedSize}B, original={OriginalSize}B, normalize={Normalize}",
            dubbedAudio.Length, originalAudio.Length, normalizeLevel);

        // Stub: return dubbed audio as-is
        await Task.CompletedTask;
        return dubbedAudio;
    }

    public async Task MuxAndExportAsync(
        string videoPath,
        byte[] audioStream,
        string outputPath,
        string codec = "h264",
        string bitrate = "5000k",
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Muxing video {VideoPath} + {AudioSize}B audio → {OutputPath} ({Codec}, {Bitrate})",
            videoPath, audioStream.Length, outputPath, codec, bitrate);

        await File.WriteAllBytesAsync(outputPath, new byte[] { 0 }, cancellationToken);
    }
}
