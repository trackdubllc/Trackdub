namespace Trackdub.Media.Services;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Media processing service for extraction, mixing, and muxing.
/// </summary>
public interface IMediaService
{
    /// <summary>Extract audio from video file.</summary>
    Task<string> ExtractAudioAsync(
        string inputPath,
        string outputFormat = "wav",
        CancellationToken cancellationToken = default);

    /// <summary>Extract video stream without audio.</summary>
    Task<string> ExtractVideoAsync(
        string inputPath,
        CancellationToken cancellationToken = default);

    /// <summary>Mix dubbed audio with original audio (optional keep original as secondary track).</summary>
    Task<byte[]> MixAudioAsync(
        byte[] dubbedAudio,
        byte[] originalAudio,
        bool keepOriginal = true,
        bool normalizeLevel = true,
        float targetLUFS = -16f,
        CancellationToken cancellationToken = default);

    /// <summary>Mux video and audio streams into output file.</summary>
    Task MuxAndExportAsync(
        string videoPath,
        byte[] audioStream,
        string outputPath,
        string codec = "h264",
        string bitrate = "5000k",
        CancellationToken cancellationToken = default);
}
