using Trackdub.Application.LipSync;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Tests;

public sealed class PhonemeTimingPlannerTests
{
    private static readonly PhonemeStretchBounds DefaultBounds =
        new(MinRatio: 0.5, MaxRatio: 2.0, PreferredMaxVowelRatio: 1.5);

    private static PhonemeTiming MakePhoneme(string symbol, double startSec, double endSec) =>
        new(symbol, "ipa", TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(endSec), 0.9);

    // ---------------------------------------------------------------------------
    // Edge cases
    // ---------------------------------------------------------------------------

    [Fact]
    public void PlanStretches_EmptyTtsPhonemes_ReturnsEmpty()
    {
        var planner = new PhonemeTimingPlanner();
        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches([], [], DefaultBounds);
        Assert.Empty(result);
    }

    [Fact]
    public void PlanStretches_NoMatchingSourceVisemes_RemainsUnaligned()
    {
        var planner = new PhonemeTimingPlanner();
        var tts = new[] { MakePhoneme("æ", 0, 0.1) };
        var source = new[] { MakePhoneme("z", 0, 0.2) };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches(source, tts, DefaultBounds);

        Assert.Single(result);
        Assert.Equal(1.0, result[0].StretchRatio);
        Assert.False(result[0].WithinBounds);
    }

    [Fact]
    public void PlanStretches_ZeroDurationTtsPhoneme_MarkedOutOfBounds()
    {
        var planner = new PhonemeTimingPlanner();
        var tts = new[] { MakePhoneme("æ", 0.1, 0.1) }; // zero duration
        var source = new[] { MakePhoneme("æ", 0, 0.2) };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches(source, tts, DefaultBounds);

        Assert.Single(result);
        Assert.Equal(1.0, result[0].StretchRatio);
        Assert.False(result[0].WithinBounds);
    }

    // ---------------------------------------------------------------------------
    // Ratio computation
    // ---------------------------------------------------------------------------

    [Fact]
    public void PlanStretches_MatchingSymbol_ComputesCorrectRatio()
    {
        var planner = new PhonemeTimingPlanner();
        // A consonant uses the general upper bound.
        var source = new[] { MakePhoneme("b", 0, 0.2) };
        var tts = new[] { MakePhoneme("b", 0, 0.1) };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches(source, tts, DefaultBounds);

        Assert.Single(result);
        Assert.Equal(2.0, result[0].StretchRatio, precision: 6);
        Assert.True(result[0].WithinBounds);
    }

    [Fact]
    public void PlanStretches_RatioClampedToMaxBound()
    {
        var planner = new PhonemeTimingPlanner();
        var source = new[] { MakePhoneme("b", 0, 1.0) };
        var tts = new[] { MakePhoneme("b", 0, 0.1) };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches(source, tts, DefaultBounds);

        Assert.Equal(2.0, result[0].StretchRatio, precision: 6);
        // Clamped → original ratio was outside bounds
        Assert.False(result[0].WithinBounds);
    }

    [Fact]
    public void PlanStretches_RatioClampedToMinBound()
    {
        var planner = new PhonemeTimingPlanner();
        // source 0.01s, TTS 0.5s → raw ratio 0.02, clamped to 0.5
        var source = new[] { MakePhoneme("æ", 0, 0.01) };
        var tts = new[] { MakePhoneme("æ", 0, 0.5) };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches(source, tts, DefaultBounds);

        Assert.Equal(0.5, result[0].StretchRatio, precision: 6);
        Assert.False(result[0].WithinBounds);
    }

    [Fact]
    public void PlanStretches_MultiplePhonemes_IndependentRatios()
    {
        var planner = new PhonemeTimingPlanner();
        var source = new[]
        {
            MakePhoneme("æ", 0.0, 0.2),   // 0.2s
            MakePhoneme("b", 0.2, 0.4),   // 0.2s
        };
        var tts = new[]
        {
            MakePhoneme("æ", 0.0, 0.1),   // 0.1s → ratio 2.0
            MakePhoneme("b", 0.1, 0.3),   // 0.2s → ratio 1.0
        };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches(source, tts, DefaultBounds);

        Assert.Equal(2, result.Count);
        Assert.Equal(1.5, result[0].StretchRatio, precision: 6);
        Assert.Equal(1.0, result[1].StretchRatio, precision: 6);
    }

    // ---------------------------------------------------------------------------
    // Ordered, cross-inventory matching
    // ---------------------------------------------------------------------------

    [Fact]
    public void PlanStretches_DuplicateSourceSymbol_UsesFirstOccurrence()
    {
        var planner = new PhonemeTimingPlanner();
        // Equal monotonic matches prefer the earliest source occurrence.
        var source = new[]
        {
            MakePhoneme("æ", 0.0, 0.12),
            MakePhoneme("æ", 0.12, 0.92),
        };
        var tts = new[] { MakePhoneme("æ", 0, 0.1) };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches(source, tts, DefaultBounds);

        Assert.Equal(1.2, result[0].StretchRatio, precision: 6);
        Assert.True(result[0].WithinBounds);
    }

    [Fact]
    public void PlanStretches_ConsumesRepeatedSourceVisemesInOrder()
    {
        var planner = new PhonemeTimingPlanner();
        var source = new[]
        {
            MakePhoneme("b", 0, 0.1),
            MakePhoneme("b", 0.1, 0.3),
        };
        var tts = new[]
        {
            MakePhoneme("p", 0, 0.1),
            MakePhoneme("m", 0.1, 0.2),
        };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches(source, tts, DefaultBounds);

        Assert.Equal(1.0, result[0].StretchRatio, 6);
        Assert.Equal(2.0, result[1].StretchRatio, 6);
    }

    [Fact]
    public void PlanStretches_MapsArpabetAndIpaAcrossLanguages()
    {
        var planner = new PhonemeTimingPlanner();
        var source = new[]
        {
            new PhonemeTiming("B", "arpabet", TimeSpan.Zero, TimeSpan.FromSeconds(0.12), 0.9),
        };
        var tts = new[] { MakePhoneme("p", 0, 0.1) };

        PhonemeStretchPlan result = Assert.Single(planner.PlanStretches(source, tts, DefaultBounds));

        Assert.True(result.WithinBounds);
        Assert.Equal(1.2, result.StretchRatio, 6);
    }

    [Fact]
    public void PlanStretches_VowelUsesPreferredUpperBound()
    {
        var planner = new PhonemeTimingPlanner();
        var source = new[] { MakePhoneme("æ", 0, 0.18) };
        var tts = new[] { MakePhoneme("e", 0, 0.1) };

        PhonemeStretchPlan result = Assert.Single(planner.PlanStretches(source, tts, DefaultBounds));

        Assert.False(result.WithinBounds);
        Assert.Equal(1.5, result.StretchRatio, 6);
    }

    // ---------------------------------------------------------------------------
    // Plan shape
    // ---------------------------------------------------------------------------

    [Fact]
    public void PlanStretches_ResultCount_MatchesTtsPhonemeCount()
    {
        var planner = new PhonemeTimingPlanner();
        var tts = new[]
        {
            MakePhoneme("a", 0, 0.1),
            MakePhoneme("b", 0.1, 0.2),
            MakePhoneme("c", 0.2, 0.3),
        };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches([], tts, DefaultBounds);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void PlanStretches_PreservesSymbolAndTimings()
    {
        var planner = new PhonemeTimingPlanner();
        var tts = new[] { MakePhoneme("æ", 0.5, 0.7) };

        IReadOnlyList<PhonemeStretchPlan> result = planner.PlanStretches([], tts, DefaultBounds);

        Assert.Equal("æ", result[0].Symbol);
        Assert.Equal(TimeSpan.FromSeconds(0.5), result[0].OriginalStart);
        Assert.Equal(TimeSpan.FromSeconds(0.7), result[0].OriginalEnd);
    }
}
