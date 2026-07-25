namespace Trackdub.Domain.Tts;

/// <summary>
/// Distinguishes between different TTS generation variants within a candidate group.
/// Primary is the default/base generation, while alternatives provide parameter variations
/// for A/B comparison during preview.
/// </summary>
public enum TtsCandidateVariant
{
    /// <summary>
    /// The default/base TTS generation with standard parameters.
    /// </summary>
    Primary = 0,

    /// <summary>
    /// First alternative variant with slight parameter adjustments (e.g., stability, speed, pitch).
    /// </summary>
    Alternative1 = 1,

    /// <summary>
    /// Second alternative variant with different parameter adjustments.
    /// </summary>
    Alternative2 = 2,

    /// <summary>
    /// An explicitly-generated A/B candidate take (created via the candidate-generation workflow).
    /// </summary>
    Candidate = 3
}