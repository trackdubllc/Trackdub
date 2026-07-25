namespace Trackdub.Inference.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Stub implementation of ITranscriptionService.
/// </summary>
public class TranscriptionService(ILogger<TranscriptionService> logger) : ITranscriptionService
{
    public async Task<List<TranscriptSegment>> TranscribeAsync(
        string audioPath,
        string language,
        bool includeTimestamps = true,
        bool includeSpeakerDiarization = true,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Transcribing audio {AudioPath} in {Language}",
            audioPath, language);

        var segments = new List<TranscriptSegment>
        {
            new()
            {
                SegmentNumber = 1,
                SpeakerId = "speaker_1",
                Text = "This is a sample transcript segment one.",
                StartTime = 0.0,
                EndTime = 2.5,
                Confidence = 0.95
            },
            new()
            {
                SegmentNumber = 2,
                SpeakerId = "speaker_2",
                Text = "And this is the second segment from a different speaker.",
                StartTime = 3.0,
                EndTime = 5.5,
                Confidence = 0.92
            }
        };

        await Task.CompletedTask;
        return segments;
    }
}
