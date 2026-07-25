namespace Trackdub.Inference.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Stub implementation of ITtsService.
/// </summary>
public class TtsService(ILogger<TtsService> logger) : ITtsService
{
    public async Task<byte[]> GenerateSpeechAsync(
        string text,
        Voice voice,
        string targetLanguage,
        double? targetDuration = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Generating speech for voice {VoiceName} ({TargetLanguage}): {TextLength} chars",
            voice.Name, targetLanguage, text.Length);

        // Return stub WAV data (silent audio)
        var waveHeader = new byte[]
        {
            0x52, 0x49, 0x46, 0x46, // "RIFF"
            0x24, 0x00, 0x00, 0x00, // File size (36 bytes)
            0x57, 0x41, 0x56, 0x45, // "WAVE"
            0x66, 0x6D, 0x74, 0x20, // "fmt "
            0x10, 0x00, 0x00, 0x00, // Subchunk1 size (16 bytes)
            0x01, 0x00,             // Audio format (1 = PCM)
            0x02, 0x00,             // Num channels (2)
            0x44, 0xAC, 0x00, 0x00, // Sample rate (44100)
            0x10, 0xB1, 0x02, 0x00, // Byte rate
            0x04, 0x00,             // Block align
            0x10, 0x00,             // Bits per sample (16)
            0x64, 0x61, 0x74, 0x61, // "data"
            0x00, 0x00, 0x00, 0x00  // Subchunk2 size (0 bytes)
        };

        await Task.CompletedTask;
        return waveHeader;
    }
}
