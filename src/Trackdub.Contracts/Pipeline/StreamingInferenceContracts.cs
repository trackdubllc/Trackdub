using System.Text;
using Trackdub.Domain;

namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Stable identity for one streamed pipeline unit: which run, artifact snapshot, stage,
/// segment, and (where applicable) transcript revision produced it, plus a monotonic
/// sequence for ordering within that stream.
/// </summary>
public sealed record PipelineStreamIdentity(
    Guid RunId,
    string SnapshotId,
    RuntimeStage Stage,
    int SegmentIndex,
    Guid? RevisionId,
    long Sequence);

/// <summary>
/// One streaming pipeline unit with its identity and an estimated byte footprint used for
/// weighted backpressure by <c>BoundedPipelineChannel</c>. Validity is enforced at channel
/// write: non-empty <see cref="PipelineStreamIdentity.RunId"/>, non-blank
/// <see cref="PipelineStreamIdentity.SnapshotId"/>, non-negative segment/sequence, positive
/// <see cref="EstimatedBytes"/>, non-null payload.
/// </summary>
public sealed record PipelineStreamItem<T>(
    PipelineStreamIdentity Identity,
    T Payload,
    int EstimatedBytes)
    where T : notnull;

/// <summary>
/// Optional streaming counterpart to <see cref="IAudioTranscriptionEngine"/>. Adapters that
/// implement this yield transcription segments progressively; the batch interface remains
/// authoritative and unchanged.
/// </summary>
public interface IStreamingAudioTranscriptionEngine : IAudioTranscriptionEngine
{
    IAsyncEnumerable<PipelineStreamItem<RecognizedTranscriptSegment>> TranscribeStreamAsync(
        AudioTranscriptionRequest request,
        Guid runId,
        string snapshotId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional streaming counterpart to <see cref="ITranslationEngine"/>. Emitted items carry
/// the source revision id so consumers can stage per-segment output without committing;
/// batch translation still owns repository commits. Consumption is opt-in — the batch path
/// remains the default.
/// </summary>
public interface IStreamingTranslationEngine : ITranslationEngine
{
    IAsyncEnumerable<PipelineStreamItem<TranslatedTextSegment>> TranslateStreamAsync(
        TranslationRequest request,
        Guid runId,
        string snapshotId,
        Guid sourceRevisionId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional streaming counterpart to <see cref="ITtsEngine"/>. Each synthesized output
/// retains the exact <see cref="PipelineStreamIdentity"/> of the request that produced it —
/// request identity is output identity.
/// </summary>
public interface IStreamingTtsEngine : ITtsEngine
{
    IAsyncEnumerable<PipelineStreamItem<TtsSynthesisResult>> SynthesizeStreamAsync(
        IAsyncEnumerable<PipelineStreamItem<TtsSynthesisRequest>> requests,
        CancellationToken cancellationToken);
}

/// <summary>
/// Builds <see cref="PipelineStreamItem{T}"/> units for translation-stage streaming with the
/// identity/estimate rules every producer must share, and validates the stream context
/// (run, snapshot, source revision) producers must reject before emitting.
/// </summary>
public static class PipelineStreamItemFactory
{
    /// <summary>Fail-fast validation for translation stream producers.</summary>
    public static void ValidateTranslationStreamContext(
        Guid runId, string snapshotId, Guid sourceRevisionId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Stream run id must be non-empty.", nameof(runId));
        }

        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            throw new ArgumentException("Stream snapshot id must be non-blank.", nameof(snapshotId));
        }

        if (sourceRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Source revision id must be non-empty.", nameof(sourceRevisionId));
        }
    }

    /// <summary>
    /// Wraps one finalized translated segment as a stream item. Sequence is the producer's
    /// monotonic emission order; the estimate is saturating UTF-8 bytes + fixed overhead.
    /// </summary>
    public static PipelineStreamItem<TranslatedTextSegment> CreateTranslation(
        TranslatedTextSegment segment,
        Guid runId,
        string snapshotId,
        Guid sourceRevisionId,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ValidateTranslationStreamContext(runId, snapshotId, sourceRevisionId);
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be non-negative.");
        }

        long estimate = (long)Encoding.UTF8.GetByteCount(segment.Text ?? string.Empty) + 64;
        int estimatedBytes = estimate > int.MaxValue ? int.MaxValue : Math.Max(1, (int)estimate);
        return new PipelineStreamItem<TranslatedTextSegment>(
            new PipelineStreamIdentity(
                runId,
                snapshotId.Trim(),
                RuntimeStage.Translation,
                segment.Index,
                sourceRevisionId,
                sequence),
            segment,
            estimatedBytes);
    }
}
