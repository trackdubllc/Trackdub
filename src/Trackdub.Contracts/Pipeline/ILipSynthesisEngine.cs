namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// M23 video lip-synthesis engine: repairs mouth motion within the original footage for a set of
/// speaker turns. Distinct from <c>IForcedAligner</c> (M22 audio) and from any future portrait
/// animation engine (M24) — this seam repairs original frames, it does not generate new performers.
/// </summary>
public interface ILipSynthesisEngine
{
    /// <summary>
    /// True only when the synthesis runtime AND model are actually present and loadable. Registered
    /// ≠ available: a gated experimental provider with no runtime reports false and never fakes it.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>True when this engine is an experimental-lane provider (UI must label it).</summary>
    bool IsExperimental { get; }

    string ProviderId { get; }
    string ModelId { get; }

    Task<LipSynthesisResult> SynthesizeTurnAsync(
        LipSynthesisRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Request to synthesize one speaker turn. The original video is the authority; the engine returns
/// a patched clip for the turn's mouth region only. Dubbed audio (improved M22 take when available,
/// else post-atempo) drives the mouth motion; a symbolic phoneme plan is intentionally optional.
/// </summary>
public sealed record LipSynthesisRequest(
    string OriginalVideoPath,
    string DubbedAudioPath,
    Guid SegmentId,
    TimeSpan TurnStart,
    TimeSpan TurnEnd,
    string? SpeakerId,
    LipSynthesisOptions Options,
    string? PreferredModelAlias = null);

public sealed record LipSynthesisOptions(
    double MinFaceConfidence = 0.65,
    double MaxYawDegrees = 30.0,
    double MaxPitchDegrees = 30.0);

public sealed record LipSynthesisResult(
    Guid SegmentId,
    LipSynthesisEngineStatus Status,
    /// <summary>Absolute path to the patched turn clip when synthesized; null otherwise.</summary>
    string? PatchedClipPath,
    string? SkipReason,
    string? FailureReason,
    string? ProviderId,
    string? ModelId);

/// <summary>Coarse engine outcome. The stage handler maps this plus face guards to the rich per-turn status.</summary>
public enum LipSynthesisEngineStatus
{
    Synthesized,
    Skipped,
    Failed
}
