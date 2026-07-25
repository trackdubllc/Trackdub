// NOTE: This stub is superseded by Wav2Vec2CtcForcedAligner (M22 Wave 3).
// Kept for reference only; not registered in CompositionRoot.

using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Onnx.ForcedAlignment;

/// <summary>
/// Stub ONNX CTC phoneme aligner.
/// Only responsibility: run the ONNX model and return raw CTC logits + vocabulary.
/// CTC trellis/path search lives in Trackdub.Application.
/// Not usable until a real manifest entry is wired and the ONNX session is bootstrapped.
/// </summary>
public sealed class OnnxCtcPhonemeAligner : IForcedAligner
{
    public Task<ForcedAlignmentResult> AlignAsync(
        ForcedAlignmentRequest request,
        CancellationToken cancellationToken)
    {
        // Wave 3 gate: real ONNX session not yet wired.
        // This stub is intentionally never called in production; the routing layer
        // will return SkippedRuntimeUnavailable before reaching this code.
        throw new NotImplementedException(
            "OnnxCtcPhonemeAligner: real ONNX session not wired. " +
            "This stub is present for DI registration only. " +
            "See M22 Wave 3 for real implementation.");
    }
}
