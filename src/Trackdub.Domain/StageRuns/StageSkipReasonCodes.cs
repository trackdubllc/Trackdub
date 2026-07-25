namespace Trackdub.Domain.StageRuns;

/// <summary>
/// Canonical stage skip reason codes emitted by the dubbing pipeline and SDK.
/// </summary>
public static class StageSkipReasonCodes
{
    public const string ExistingArtifactsValid = "EXISTING_ARTIFACTS_VALID";

    public const string PrerequisiteFailed = "PREREQUISITE_FAILED";

    public const string NoTranscriptSegments = "NO_TRANSCRIPT_SEGMENTS";

    public const string NoSpeechRegions = "NO_SPEECH_REGIONS";

    private static readonly HashSet<string> BenignSkipReasonCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ExistingArtifactsValid,
        PrerequisiteFailed,
        NoTranscriptSegments,
        NoSpeechRegions,
    };

    /// <summary>
    /// Skip reason codes that represent intentional resume/prerequisite gating rather than
    /// a failed attempt to run a requested stage.
    /// </summary>
    public static bool IsBenignSkip(string? reasonCode) =>
        reasonCode is not null && BenignSkipReasonCodes.Contains(reasonCode);
}
