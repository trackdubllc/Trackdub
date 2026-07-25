namespace Trackdub.Contracts.Pipeline;

public interface IForcedAligner
{
    Task<ForcedAlignmentResult> AlignAsync(
        ForcedAlignmentRequest request,
        CancellationToken cancellationToken);
}

public sealed record ForcedAlignmentRequest(
    string AudioPath,
    string NormalizedTranscript,
    string? LanguageCode,
    string SegmentId,
    ForcedAlignmentOptions Options);

public sealed record ForcedAlignmentResult(
    string SegmentId,
    ForcedAlignmentStatus Status,
    IReadOnlyList<WordTiming> Words,
    IReadOnlyList<PhonemeTiming> Phonemes,
    AlignmentConfidence Confidence,
    string? SkipReason,
    string? ProviderId,
    string? ModelId);

public enum ForcedAlignmentStatus
{
    Success,
    Partial,
    Skipped,
    Failed
}

public sealed record WordTiming(
    string Text,
    TimeSpan Start,
    TimeSpan End,
    double Confidence);

public sealed record PhonemeTiming(
    string Symbol,
    string Inventory,
    TimeSpan Start,
    TimeSpan End,
    double Confidence,
    string? WordText = null);

public sealed record ForcedAlignmentOptions(
    bool AllowPartial = true,
    double MinOverallConfidence = 0.65,
    double MinPhonemeConfidence = 0.50,
    /// <summary>
    /// When true, routing must select an adapter that emits phoneme-level timings.
    /// Word-level-only aligners are not acceptable; if no phoneme-capable adapter is
    /// available the request is skipped with a structured reason instead of returning
    /// a result with empty phonemes.
    /// </summary>
    bool RequirePhonemeTimings = false,
    /// <summary>
    /// Optional model id or provider id to prefer during routing. Falls back to
    /// capability/registration-order selection when the preferred model is unavailable.
    /// </summary>
    string? PreferredModelAlias = null);

public interface IPhonemeInventoryMapper
{
    /// <summary>Normalize a raw phoneme symbol to a canonical inventory token.</summary>
    string? MapSymbol(string rawSymbol, string sourceInventory, string targetInventory);
}

public interface IPhonemeTimingPlanner
{
    /// <summary>
    /// Given source and TTS phoneme timings, compute a per-phoneme stretch plan.
    /// Returns null for each phoneme that cannot be reliably planned.
    /// </summary>
    IReadOnlyList<PhonemeStretchPlan> PlanStretches(
        IReadOnlyList<PhonemeTiming> sourcePhonemes,
        IReadOnlyList<PhonemeTiming> ttsPhonemes,
        PhonemeStretchBounds bounds);
}

public sealed record PhonemeStretchPlan(
    string Symbol,
    TimeSpan OriginalStart,
    TimeSpan OriginalEnd,
    double StretchRatio,
    bool WithinBounds);

public interface IPhonemeStretchService
{
    /// <summary>
    /// Apply phoneme-level time-stretching to a WAV file; write result to <paramref name="outputPath"/>.
    /// Returns the aligned duration, or null if the stretch was skipped.
    /// </summary>
    Task<TimeSpan?> StretchAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<PhonemeStretchPlan> plan,
        CancellationToken cancellationToken);
}

public sealed record AlignmentConfidence(
    double Overall,
    double? WordLevelMean,
    double? PhonemeLevelMean);

public sealed record PhonemeStretchBounds(
    double MinRatio,
    double MaxRatio,
    double PreferredMaxVowelRatio);
