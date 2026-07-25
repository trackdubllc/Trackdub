namespace Trackdub.Application.LipSync;

using Trackdub.Contracts.Pipeline;

/// <summary>
/// Matches TTS phonemes to source phonemes by symbol identity and computes per-phoneme
/// stretch ratios. Unmatched phonemes fall back to a ratio of 1.0 (no stretch).
/// </summary>
public sealed class PhonemeTimingPlanner : IPhonemeTimingPlanner
{
    public IReadOnlyList<PhonemeStretchPlan> PlanStretches(
        IReadOnlyList<PhonemeTiming> sourcePhonemes,
        IReadOnlyList<PhonemeTiming> ttsPhonemes,
        PhonemeStretchBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(sourcePhonemes);
        ArgumentNullException.ThrowIfNull(ttsPhonemes);
        ArgumentNullException.ThrowIfNull(bounds);

        if (ttsPhonemes.Count == 0)
            return [];

        // Build a lookup of source phoneme durations by symbol (first occurrence wins).
        Dictionary<string, TimeSpan> sourceDurationBySymbol = BuildSourceDurationMap(sourcePhonemes);

        List<PhonemeStretchPlan> plan = new(ttsPhonemes.Count);

        foreach (PhonemeTiming tts in ttsPhonemes)
        {
            TimeSpan ttsDuration = tts.End - tts.Start;

            // Skip zero-duration TTS phonemes — no meaningful ratio can be derived.
            if (ttsDuration <= TimeSpan.Zero)
            {
                plan.Add(new PhonemeStretchPlan(
                    tts.Symbol, tts.Start, tts.End,
                    StretchRatio: 1.0, WithinBounds: false));
                continue;
            }

            double ratio = 1.0;
            bool withinBounds = true;

            if (sourceDurationBySymbol.TryGetValue(tts.Symbol, out TimeSpan sourceDuration)
                && sourceDuration > TimeSpan.Zero)
            {
                double rawRatio = sourceDuration.TotalSeconds / ttsDuration.TotalSeconds;
                withinBounds = rawRatio >= bounds.MinRatio && rawRatio <= bounds.MaxRatio;
                ratio = Clamp(rawRatio, bounds);
            }

            plan.Add(new PhonemeStretchPlan(tts.Symbol, tts.Start, tts.End, ratio, withinBounds));
        }

        return plan;
    }

    private static Dictionary<string, TimeSpan> BuildSourceDurationMap(
        IReadOnlyList<PhonemeTiming> sourcePhonemes)
    {
        Dictionary<string, TimeSpan> map = new(StringComparer.Ordinal);
        foreach (PhonemeTiming p in sourcePhonemes)
        {
            TimeSpan duration = p.End - p.Start;
            if (duration > TimeSpan.Zero && !map.ContainsKey(p.Symbol))
                map[p.Symbol] = duration;
        }
        return map;
    }

    private static double Clamp(double ratio, PhonemeStretchBounds bounds) =>
        Math.Max(bounds.MinRatio, Math.Min(bounds.MaxRatio, ratio));
}
