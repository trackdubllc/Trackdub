using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakePhonemeTimingPlanner : IPhonemeTimingPlanner
{
    public bool ReturnEmptyPlan { get; set; }

    public IReadOnlyList<PhonemeStretchPlan> PlanStretches(
        IReadOnlyList<PhonemeTiming> sourcePhonemes,
        IReadOnlyList<PhonemeTiming> ttsPhonemes,
        PhonemeStretchBounds bounds)
    {
        if (ReturnEmptyPlan)
            return [];

        // Return a trivial 1:1 plan for each TTS phoneme (ratio 1.0, within bounds).
        return ttsPhonemes
            .Select(p => new PhonemeStretchPlan(p.Symbol, p.Start, p.End, StretchRatio: 1.0, WithinBounds: true))
            .ToList();
    }
}
