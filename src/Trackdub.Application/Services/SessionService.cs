namespace Trackdub.Application.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Stub implementation of ISessionService.
/// </summary>
public class SessionService(ILogger<SessionService> logger) : ISessionService
{
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly Dictionary<string, List<TranscriptSegment>> _transcripts = new();
    private readonly Dictionary<string, List<TranslatedSegment>> _translations = new();
    private readonly Dictionary<string, Dictionary<string, Voice>> _voiceAssignments = new();
    private readonly Dictionary<string, List<AudioSegment>> _dubbedAudio = new();

    public async Task<Session> CreateSessionAsync(
        Project project,
        string sourceLanguage,
        string targetLanguage,
        string jobName)
    {
        var session = new Session
        {
            JobId = jobName,
            ProjectId = project.Id,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage
        };

        _sessions[session.Id] = session;
        logger.LogInformation("Created session {SessionId} for project {ProjectId}", session.Id, project.Id);
        await Task.CompletedTask;
        return session;
    }

    public async Task SetTranscriptAsync(string sessionId, List<TranscriptSegment> segments)
    {
        _transcripts[sessionId] = segments;
        logger.LogInformation("Stored {SegmentCount} transcript segments in session {SessionId}", segments.Count, sessionId);
        await Task.CompletedTask;
    }

    public async Task<List<TranscriptSegment>> GetTranscriptAsync(string sessionId)
    {
        _transcripts.TryGetValue(sessionId, out var segments);
        await Task.CompletedTask;
        return segments ?? new();
    }

    public async Task SetTranslatedTranscriptAsync(string sessionId, List<TranslatedSegment> segments)
    {
        _translations[sessionId] = segments;
        logger.LogInformation("Stored {SegmentCount} translated segments in session {SessionId}", segments.Count, sessionId);
        await Task.CompletedTask;
    }

    public async Task<List<TranslatedSegment>> GetTranslatedTranscriptAsync(string sessionId)
    {
        _translations.TryGetValue(sessionId, out var segments);
        await Task.CompletedTask;
        return segments ?? new();
    }

    public async Task SetVoiceAssignmentsAsync(string sessionId, Dictionary<string, Voice> assignments)
    {
        _voiceAssignments[sessionId] = assignments;
        logger.LogInformation("Stored {VoiceCount} voice assignments in session {SessionId}", assignments.Count, sessionId);
        await Task.CompletedTask;
    }

    public async Task<Dictionary<string, Voice>> GetVoiceAssignmentsAsync(string sessionId)
    {
        _voiceAssignments.TryGetValue(sessionId, out var assignments);
        await Task.CompletedTask;
        return assignments ?? new();
    }

    public async Task SetDubbedAudioAsync(string sessionId, List<AudioSegment> segments)
    {
        _dubbedAudio[sessionId] = segments;
        logger.LogInformation("Stored {SegmentCount} dubbed audio segments in session {SessionId}", segments.Count, sessionId);
        await Task.CompletedTask;
    }

    public async Task<List<AudioSegment>> GetDubbedAudioAsync(string sessionId)
    {
        _dubbedAudio.TryGetValue(sessionId, out var segments);
        await Task.CompletedTask;
        return segments ?? new();
    }

    public async Task<List<Speaker>> GetSpeakersAsync(string sessionId)
    {
        var speakers = new List<Speaker>
        {
            new() { Id = "speaker_1", Label = "Speaker 1", SpeakerNumber = 1 },
            new() { Id = "speaker_2", Label = "Speaker 2", SpeakerNumber = 2 }
        };
        await Task.CompletedTask;
        return speakers;
    }
}
