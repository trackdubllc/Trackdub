using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

/// <summary>
/// Deterministic test double for <see cref="ILipSynthesisEngine"/>. Performs no real video,
/// image, model, or network I/O. By default it "synthesizes" by writing a small placeholder file
/// at a per-turn output path and returning <see cref="LipSynthesisEngineStatus.Synthesized"/>.
/// Configure the properties to exercise skip/failure/unavailable paths.
/// </summary>
public sealed class FakeLipSynthesisEngine : ILipSynthesisEngine
{
    public bool Available { get; set; } = true;
    public bool Experimental { get; set; }
    public bool ThrowOnSynthesize { get; set; }
    public LipSynthesisEngineStatus StatusToReturn { get; set; } = LipSynthesisEngineStatus.Synthesized;
    public string? SkipReasonToReturn { get; set; }
    public string ProviderIdValue { get; set; } = "fake-lip-synthesis-engine";
    public string ModelIdValue { get; set; } = "fake-lip-synthesis-model";

    public int CallCount { get; private set; }
    public List<LipSynthesisRequest> Requests { get; } = [];

    /// <summary>
    /// Optional per-turn output directory. When set, a placeholder patched clip is written here so
    /// the stage handler can register and preserve a real on-disk artifact path.
    /// </summary>
    public string? OutputDirectory { get; set; }

    public bool IsAvailable => Available;
    public bool IsExperimental => Experimental;
    public string ProviderId => ProviderIdValue;
    public string ModelId => ModelIdValue;

    public Task<LipSynthesisResult> SynthesizeTurnAsync(
        LipSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        Requests.Add(request);
        CallCount++;

        if (ThrowOnSynthesize)
            throw new InvalidOperationException("FakeLipSynthesisEngine: simulated synthesis failure.");

        if (StatusToReturn != LipSynthesisEngineStatus.Synthesized)
        {
            return Task.FromResult(new LipSynthesisResult(
                SegmentId: request.SegmentId,
                Status: StatusToReturn,
                PatchedClipPath: null,
                SkipReason: SkipReasonToReturn,
                FailureReason: StatusToReturn == LipSynthesisEngineStatus.Failed
                    ? "FakeLipSynthesisEngine: configured failure."
                    : null,
                ProviderId: ProviderIdValue,
                ModelId: ModelIdValue));
        }

        string? patchedClipPath = null;
        if (OutputDirectory is not null)
        {
            Directory.CreateDirectory(OutputDirectory);
            patchedClipPath = Path.Combine(OutputDirectory, $"patched-{request.SegmentId:N}.mp4");
            // Placeholder bytes only — the fake never copies or touches the source video.
            File.WriteAllBytes(patchedClipPath, [0x00]);
        }

        return Task.FromResult(new LipSynthesisResult(
            SegmentId: request.SegmentId,
            Status: LipSynthesisEngineStatus.Synthesized,
            PatchedClipPath: patchedClipPath,
            SkipReason: null,
            FailureReason: null,
            ProviderId: ProviderIdValue,
            ModelId: ModelIdValue));
    }
}
