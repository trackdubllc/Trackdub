namespace Trackdub.Contracts.Pipeline;

public interface IForcedAlignerAdapter : IForcedAligner
{
    string ProviderId { get; }
    string ModelId { get; }

    /// <summary>
    /// Returns false when the required model files are not present or have not been verified.
    /// Never throws — callers depend on this for safe availability checks.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// True when the adapter emits phoneme-level timings in
    /// <c>ForcedAlignmentResult.Phonemes</c>. Word-level-only aligners must return false
    /// so phoneme-dependent stages (lip-sync) can route past them.
    /// </summary>
    bool SupportsPhonemeTimings { get; }
}
