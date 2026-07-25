namespace Trackdub.Application.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Stub implementation of IVoiceAssignmentService.
/// </summary>
public class VoiceAssignmentService(ILogger<VoiceAssignmentService> logger) : IVoiceAssignmentService
{
    public async Task<VoiceAnalysis> AnalyzeSpeakersAsync(
        string mediaPath,
        List<Speaker> speakers,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Analyzing {SpeakerCount} speakers from {MediaPath}", speakers.Count, mediaPath);

        var analysis = new VoiceAnalysis();
        foreach (var speaker in speakers)
        {
            analysis.Speakers[speaker.Id] = new SpeakerCharacteristics
            {
                Gender = speaker.SpeakerNumber % 2 == 0 ? "female" : "male",
                ApproximateAge = 30 + (speaker.SpeakerNumber * 5),
                Accent = "neutral",
                Confidence = 0.85f
            };
        }

        await Task.CompletedTask;
        return analysis;
    }

    public async Task<Dictionary<string, Voice>> AssignVoicesAsync(
        VoiceAnalysis analysis,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Assigning voices for {SpeakerCount} speakers to {TargetLanguage}",
            analysis.Speakers.Count, targetLanguage);

        var assignments = new Dictionary<string, Voice>();
        var voiceIndex = 0;

        foreach (var speakerId in analysis.Speakers.Keys)
        {
            var characteristics = analysis.Speakers[speakerId];
            assignments[speakerId] = new Voice
            {
                Name = $"Voice_{voiceIndex:D2}_{targetLanguage}",
                Language = targetLanguage,
                Gender = characteristics.Gender,
                Age = characteristics.ApproximateAge,
                Accent = characteristics.Accent
            };
            voiceIndex++;
        }

        await Task.CompletedTask;
        return assignments;
    }

    public async Task<Dictionary<string, Voice>> ApplyRulesAsync(
        Dictionary<string, Voice> assignments,
        VoicePreferences? preferences = null)
    {
        logger.LogInformation("Applying voice assignment rules");

        if (preferences?.SpeakerVoiceOverrides != null)
        {
            foreach (var (speakerId, voiceId) in preferences.SpeakerVoiceOverrides)
            {
                if (assignments.TryGetValue(speakerId, out var existingVoice))
                {
                    existingVoice.Id = voiceId;
                    logger.LogInformation("Applied voice override for speaker {SpeakerId}", speakerId);
                }
            }
        }

        await Task.CompletedTask;
        return assignments;
    }
}
