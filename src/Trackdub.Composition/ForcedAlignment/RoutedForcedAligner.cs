using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Composition.ForcedAlignment;

/// <summary>
/// Routes forced-alignment requests to an available <see cref="IForcedAlignerAdapter"/>.
/// Selection order: explicit <c>Options.PreferredModelAlias</c> match first, then — when
/// <c>Options.RequirePhonemeTimings</c> is set — the first phoneme-capable adapter, then
/// registration order (caller controls via DI order). When phoneme timings are required and
/// no phoneme-capable adapter is installed, the request is skipped with a structured reason
/// instead of silently running a word-level aligner that would return zero phonemes.
/// Never throws on no-adapter or adapter failure — always returns a structured result.
/// </summary>
public sealed class RoutedForcedAligner : IForcedAligner
{
    private static readonly IReadOnlyList<WordTiming> NoWords = [];
    private static readonly IReadOnlyList<PhonemeTiming> NoPhonemes = [];
    private static readonly AlignmentConfidence ZeroConfidence = new(0d, null, null);

    private readonly IReadOnlyList<IForcedAlignerAdapter> _adapters;
    private readonly IApplicationLogger? _logger;

    public RoutedForcedAligner(
        IEnumerable<IForcedAlignerAdapter> adapters,
        IApplicationLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters as IReadOnlyList<IForcedAlignerAdapter> ?? [.. adapters];
        _logger = logger;
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
            _logger?.LogWarning(
                $"Forced alignment degraded via adapter '{adapter.ProviderId}/{adapter.ModelId}': {ex.Message}",
                ex);

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
        // 1. Explicit preference: match on model id or provider id, availability required.
        if (!string.IsNullOrWhiteSpace(options.PreferredModelAlias))
        {
            IForcedAlignerAdapter? preferred = _adapters.FirstOrDefault(a =>
                a.IsAvailable &&
                (string.Equals(a.ModelId, options.PreferredModelAlias, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(a.ProviderId, options.PreferredModelAlias, StringComparison.OrdinalIgnoreCase)));

            if (preferred is not null)
            {
                if (!options.RequirePhonemeTimings || preferred.SupportsPhonemeTimings)
                    return preferred;

                _logger?.LogWarning(
                    $"Preferred forced-alignment model '{options.PreferredModelAlias}' is word-level only; falling back to phoneme-capable selection.");
            }
            else
            {
                _logger?.LogWarning(
                    $"Preferred forced-alignment model '{options.PreferredModelAlias}' is not available; falling back to capability-based selection.");
            }
        }

        // 2. Capability gate: phoneme-dependent requests must not land on word-level aligners.
        if (options.RequirePhonemeTimings)
            return _adapters.FirstOrDefault(static a => a.IsAvailable && a.SupportsPhonemeTimings);

        // 3. Default: registration order.
        return _adapters.FirstOrDefault(static a => a.IsAvailable);
    }
}
