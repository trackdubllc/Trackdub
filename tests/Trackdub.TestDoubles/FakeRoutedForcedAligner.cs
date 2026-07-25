using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

/// <summary>
/// Test double for <c>RoutedForcedAligner</c>. Applies the same selection logic
/// (preferred alias → phoneme-capability gate → registration order) without any real
/// model or infrastructure dependency. Keep in sync with the real router's SelectAdapter.
/// </summary>
public sealed class FakeRoutedForcedAligner : IForcedAligner
{
    private static readonly IReadOnlyList<WordTiming> NoWords = [];
    private static readonly IReadOnlyList<PhonemeTiming> NoPhonemes = [];
    private static readonly AlignmentConfidence ZeroConfidence = new(0d, null, null);

    private readonly IReadOnlyList<IForcedAlignerAdapter> _adapters;

    public FakeRoutedForcedAligner(IReadOnlyList<IForcedAlignerAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters;
    }

    public string ProviderId =>
        _adapters.FirstOrDefault(static a => a.IsAvailable)?.ProviderId ?? "none";

    public string ModelId =>
        _adapters.FirstOrDefault(static a => a.IsAvailable)?.ModelId ?? "none";

    public async Task<ForcedAlignmentResult> AlignAsync(
        ForcedAlignmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        IForcedAlignerAdapter? adapter = SelectAdapter(request.Options);

        if (adapter is null)
        {
            bool anyAvailable = _adapters.Any(static a => a.IsAvailable);
            string skipReason = anyAvailable && request.Options.RequirePhonemeTimings
                ? "No phoneme-capable forced-alignment model is installed. The installed aligner is word-level only; download the wav2vec2-lv60-espeak-cv-ft model from the Model Manager."
                : "No forced-alignment model is installed and verified. Download a model from the Model Manager.";

            return new ForcedAlignmentResult(
                SegmentId: request.SegmentId,
                Status: ForcedAlignmentStatus.Skipped,
                Words: NoWords,
                Phonemes: NoPhonemes,
                Confidence: ZeroConfidence,
                SkipReason: skipReason,
                ProviderId: "none",
                ModelId: "none");
        }

        try
        {
            return await adapter.AlignAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ForcedAlignmentResult(
                SegmentId: request.SegmentId,
                Status: ForcedAlignmentStatus.Failed,
                Words: NoWords,
                Phonemes: NoPhonemes,
                Confidence: ZeroConfidence,
                SkipReason: ex.Message,
                ProviderId: adapter.ProviderId,
                ModelId: adapter.ModelId);
        }
    }

    private IForcedAlignerAdapter? SelectAdapter(ForcedAlignmentOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PreferredModelAlias))
        {
            IForcedAlignerAdapter? preferred = _adapters.FirstOrDefault(a =>
                a.IsAvailable &&
                (string.Equals(a.ModelId, options.PreferredModelAlias, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(a.ProviderId, options.PreferredModelAlias, StringComparison.OrdinalIgnoreCase)));

            if (preferred is not null)
                return preferred;
        }

        if (options.RequirePhonemeTimings)
            return _adapters.FirstOrDefault(static a => a.IsAvailable && a.SupportsPhonemeTimings);

        return _adapters.FirstOrDefault(static a => a.IsAvailable);
    }
}
