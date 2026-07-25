namespace Trackdub.Inference.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Speech-to-text inference service.
/// </summary>
public interface ITranscriptionService
{
    /// <summary>Transcribe audio to text with speaker diarization and timing.</summary>
    Task<List<TranscriptSegment>> TranscribeAsync(
        string audioPath,
        string language,
        bool includeTimestamps = true,
        bool includeSpeakerDiarization = true,
        CancellationToken cancellationToken = default);
}
