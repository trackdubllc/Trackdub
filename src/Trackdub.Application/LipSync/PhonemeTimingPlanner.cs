using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.LipSync;

/// <summary>Aligns source and dubbed phonemes monotonically through shared viseme classes.</summary>
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

        PhonemeTiming[] source = [.. sourcePhonemes.OrderBy(static p => p.Start)];
        PhonemeTiming[] target = [.. ttsPhonemes.OrderBy(static p => p.Start)];
        string?[] sourceVisemes = source.Select(static p => VisemeClassMapper.Map(p)).ToArray();
        string?[] targetVisemes = target.Select(static p => VisemeClassMapper.Map(p)).ToArray();
        int[,] scores = new int[source.Length + 1, target.Length + 1];
        const int gapPenalty = -1;

        for (int i = 1; i <= source.Length; i++) scores[i, 0] = i * gapPenalty;
        for (int j = 1; j <= target.Length; j++) scores[0, j] = j * gapPenalty;

        for (int i = 1; i <= source.Length; i++)
        {
            for (int j = 1; j <= target.Length; j++)
            {
                bool same = sourceVisemes[i - 1] is not null &&
                    sourceVisemes[i - 1] == targetVisemes[j - 1];
                int diagonal = scores[i - 1, j - 1] + (same ? 3 : -3);
                scores[i, j] = Math.Max(diagonal,
                    Math.Max(scores[i - 1, j] + gapPenalty, scores[i, j - 1] + gapPenalty));
            }
        }

        int?[] matchedSource = new int?[target.Length];
        int row = source.Length;
        int column = target.Length;
        while (row > 0 && column > 0)
        {
            bool same = sourceVisemes[row - 1] is not null &&
                sourceVisemes[row - 1] == targetVisemes[column - 1];
            if (scores[row, column] == scores[row - 1, column] + gapPenalty)
            {
                // Prefer the earlier source event when equivalent alignments tie.
                row--;
            }
            else if (same && scores[row, column] == scores[row - 1, column - 1] + 3)
            {
                matchedSource[column - 1] = row - 1;
                row--;
                column--;
            }
            else
            {
                column--;
            }
        }

        var plan = new List<PhonemeStretchPlan>(target.Length);
        for (int j = 0; j < target.Length; j++)
        {
            PhonemeTiming tts = target[j];
            TimeSpan ttsDuration = tts.End - tts.Start;
            if (matchedSource[j] is not int index || ttsDuration <= TimeSpan.Zero ||
                source[index].End <= source[index].Start)
            {
                plan.Add(new(tts.Symbol, tts.Start, tts.End, 1.0, false,
                    ttsDuration <= TimeSpan.Zero ? "invalid_duration" : "unmatched_viseme"));
                continue;
            }

            double rawRatio = (source[index].End - source[index].Start).TotalSeconds /
                ttsDuration.TotalSeconds;
            double upper = VisemeClassMapper.IsVowel(targetVisemes[j]!)
                ? Math.Max(bounds.MinRatio, Math.Min(bounds.MaxRatio, bounds.PreferredMaxVowelRatio))
                : bounds.MaxRatio;
            bool withinBounds = rawRatio >= bounds.MinRatio && rawRatio <= upper;
            plan.Add(new(tts.Symbol, tts.Start, tts.End,
                Math.Clamp(rawRatio, bounds.MinRatio, upper), withinBounds,
                withinBounds ? null : "unsafe_ratio"));
        }

        return plan;
    }
}
