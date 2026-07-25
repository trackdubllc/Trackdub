namespace Trackdub.Application.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Assigns voices to speakers for target language dubbing.
/// </summary>
public interface IVoiceAssignmentService
{
    /// <summary>Analyze speaker characteristics (gender, age, accent, emotion).</summary>
    Task<VoiceAnalysis> AnalyzeSpeakersAsync(
        string mediaPath,
        List<Speaker> speakers,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>Assign compatible voices from target language pool.</summary>
    Task<Dictionary<string, Voice>> AssignVoicesAsync(
        VoiceAnalysis analysis,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>Apply user preferences and rules to voice assignments.</summary>
    Task<Dictionary<string, Voice>> ApplyRulesAsync(
        Dictionary<string, Voice> assignments,
        VoicePreferences? preferences = null);
}
