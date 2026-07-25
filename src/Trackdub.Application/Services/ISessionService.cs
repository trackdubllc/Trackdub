namespace Trackdub.Application.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Manages dubbing sessions and persists transcript/audio state.
/// </summary>
public interface ISessionService
{
    /// <summary>Create session for dubbing job.</summary>
    Task<Session> CreateSessionAsync(
        Project project,
        string sourceLanguage,
        string targetLanguage,
        string jobName);

    /// <summary>Store transcript segments.</summary>
    Task SetTranscriptAsync(string sessionId, List<TranscriptSegment> segments);

    /// <summary>Retrieve transcript segments.</summary>
    Task<List<TranscriptSegment>> GetTranscriptAsync(string sessionId);

    /// <summary>Store translated transcript.</summary>
    Task SetTranslatedTranscriptAsync(string sessionId, List<TranslatedSegment> segments);

    /// <summary>Retrieve translated segments.</summary>
    Task<List<TranslatedSegment>> GetTranslatedTranscriptAsync(string sessionId);

    /// <summary>Store voice assignments (speaker → voice mapping).</summary>
    Task SetVoiceAssignmentsAsync(string sessionId, Dictionary<string, Voice> assignments);

    /// <summary>Retrieve voice assignments.</summary>
    Task<Dictionary<string, Voice>> GetVoiceAssignmentsAsync(string sessionId);

    /// <summary>Store generated dubbed audio segments.</summary>
    Task SetDubbedAudioAsync(string sessionId, List<AudioSegment> segments);

    /// <summary>Retrieve dubbed audio.</summary>
    Task<List<AudioSegment>> GetDubbedAudioAsync(string sessionId);

    /// <summary>Get speakers detected in session.</summary>
    Task<List<Speaker>> GetSpeakersAsync(string sessionId);
}
