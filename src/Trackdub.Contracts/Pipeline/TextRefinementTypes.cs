namespace Trackdub.Contracts.Pipeline;

public enum TextRefinementScope
{
    Asr = 0,
    Translation = 1
}

public enum TranscriptSegmentTextSource
{
    RawAsr = 0,
    PolishedAsr = 1
}

public enum TextRefinementGuardStatus
{
    Accepted = 0,
    Rejected = 1,
    Unchanged = 2
}

/// <summary>
/// Wrapper-generated correction codes only. Never populated from model self-reporting.
/// </summary>
public static class TextRefinementCorrectionCodes
{
    public const string ModelPolishApplied = "MODEL_POLISH_APPLIED";
    public const string FallbackUnchanged = "FALLBACK_UNCHANGED";
    public const string SpecialTokenStripped = "SPECIAL_TOKEN_STRIPPED";
    public const string LengthGuardTriggered = "LENGTH_GUARD_TRIGGERED";
    public const string NameNumberGuardTriggered = "NAME_NUMBER_GUARD_TRIGGERED";
    public const string ExplanationOutputRejected = "EXPLANATION_OUTPUT_REJECTED";
    public const string FormatGuardTriggered = "FORMAT_GUARD_TRIGGERED";
}
