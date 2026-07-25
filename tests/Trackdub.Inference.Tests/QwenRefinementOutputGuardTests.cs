using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.QwenTextRefinement;

namespace Trackdub.Inference.Tests;

public sealed class QwenRefinementOutputGuardTests
{
    [Fact]
    public void Evaluate_accepts_clean_polish_that_differs_from_input()
    {
        const string original =
            "The quick brown fox jumps over the lazy dog in the meadow today.";
        const string polished =
            "The quick brown fox jumps over the lazy dog in the meadow today!";

        QwenRefinementGuardResult result = QwenRefinementOutputGuard.Evaluate(original, polished);

        Assert.True(result.Accepted);
        Assert.Equal(TextRefinementGuardStatus.Accepted, result.GuardStatus);
        Assert.Equal(polished, result.DisplayedText);
        Assert.Contains(TextRefinementCorrectionCodes.ModelPolishApplied, result.AppliedCorrections);
    }

    [Fact]
    public void Evaluate_falls_back_when_output_matches_input()
    {
        QwenRefinementGuardResult result = QwenRefinementOutputGuard.Evaluate(
            "Hello world.",
            "Hello world.");

        Assert.False(result.Accepted);
        Assert.Equal(TextRefinementGuardStatus.Unchanged, result.GuardStatus);
        Assert.Equal("Hello world.", result.DisplayedText);
        Assert.Contains(TextRefinementCorrectionCodes.FallbackUnchanged, result.AppliedCorrections);
    }

    [Fact]
    public void Evaluate_rejects_explanation_output()
    {
        QwenRefinementGuardResult result = QwenRefinementOutputGuard.Evaluate(
            "hello world",
            "Here is the polished text: Hello world.");

        Assert.False(result.Accepted);
        Assert.Equal(TextRefinementGuardStatus.Rejected, result.GuardStatus);
        Assert.Equal("hello world", result.DisplayedText);
        Assert.Contains(TextRefinementCorrectionCodes.FallbackUnchanged, result.AppliedCorrections);
        Assert.Contains(TextRefinementCorrectionCodes.ExplanationOutputRejected, result.AppliedCorrections);
    }

    [Fact]
    public void Evaluate_rejects_speaker_label_formatting()
    {
        QwenRefinementGuardResult result = QwenRefinementOutputGuard.Evaluate(
            "hello world",
            "Speaker 1: Hello world.");

        Assert.False(result.Accepted);
        Assert.Contains(TextRefinementCorrectionCodes.FormatGuardTriggered, result.AppliedCorrections);
    }

    [Fact]
    public void Evaluate_rejects_numeric_token_changes()
    {
        const string original =
            "Order item 42 was delivered on Tuesday morning to the office downtown.";
        const string polished =
            "Order item 43 was delivered on Tuesday morning to the office downtown.";

        QwenRefinementGuardResult result = QwenRefinementOutputGuard.Evaluate(original, polished);

        Assert.False(result.Accepted);
        Assert.Contains(TextRefinementCorrectionCodes.FallbackUnchanged, result.AppliedCorrections);
        Assert.Contains(TextRefinementCorrectionCodes.NameNumberGuardTriggered, result.AppliedCorrections);
    }
    [Fact]
    public void Evaluate_throws_for_null_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => QwenRefinementOutputGuard.Evaluate(null!, "polished"));
        Assert.Throws<ArgumentNullException>(() => QwenRefinementOutputGuard.Evaluate("original", null!));
    }

    [Fact]
    public void Evaluate_falls_back_when_output_is_empty()
    {
        QwenRefinementGuardResult result = QwenRefinementOutputGuard.Evaluate("Hello world.", "   ");

        Assert.False(result.Accepted);
        Assert.Equal(TextRefinementGuardStatus.Rejected, result.GuardStatus);
        Assert.Equal("Hello world.", result.DisplayedText);
        Assert.Contains(TextRefinementCorrectionCodes.ExplanationOutputRejected, result.AppliedCorrections);
    }


}
