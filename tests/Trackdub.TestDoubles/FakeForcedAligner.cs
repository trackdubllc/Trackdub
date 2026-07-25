using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeForcedAligner : IForcedAligner, IStageRuntimeExecutionReporter
{
    public bool ThrowOnAlign { get; set; }
    public ForcedAlignmentStatus StatusToReturn { get; set; } = ForcedAlignmentStatus.Success;
    public string? SkipReasonToReturn { get; set; }
    public double OverallConfidence { get; set; } = 0.90;
    public string ProviderId { get; set; } = "fake-forced-aligner";
    public string ModelId { get; set; } = "fake-aligner-model";
    public int CallCount { get; private set; }
    public ForcedAlignmentRequest? LastRequest { get; private set; }
    /// <summary>Every request received, in call order (TTS alignment first, then source alignment).</summary>
    public List<ForcedAlignmentRequest> Requests { get; } = [];
    public IReadOnlyList<PhonemeTiming> PhonesToReturn { get; set; } = [];

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public Task<ForcedAlignmentResult> AlignAsync(
        ForcedAlignmentRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        Requests.Add(request);
        CallCount++;

        if (ThrowOnAlign)
            throw new InvalidOperationException("FakeForcedAligner: simulated failure.");

        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: ProviderId,
            SelectedProvider: ProviderId,
            ModelId: ModelId);

        var confidence = new AlignmentConfidence(OverallConfidence, null, null);
        var result = new ForcedAlignmentResult(
            SegmentId: request.SegmentId,
            Status: StatusToReturn,
            Words: [],
            Phonemes: PhonesToReturn,
            Confidence: confidence,
            SkipReason: SkipReasonToReturn,
            ProviderId: ProviderId,
            ModelId: ModelId);

        return Task.FromResult(result);
    }
}
