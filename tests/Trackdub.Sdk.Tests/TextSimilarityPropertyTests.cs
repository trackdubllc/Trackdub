using FsCheck;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Property-based tests for <see cref="TextSimilarity"/>.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Feature", "headless-pipeline-integration-test")]
public sealed class TextSimilarityPropertyTests
{
    /// <summary>
    /// Property 1: WordLevel(text, text) == 1.0 for any non-empty text.
    /// **Validates: Requirements 8.2**
    /// </summary>
    [Property(DisplayName = "Property 1: TextSimilarity identity")]
    public Property Identity_SameText_ReturnsOne(NonEmptyString text)
    {
        var value = text.Get;
        var similarity = TextSimilarity.WordLevel(value, value);
        return (similarity == 1.0).When(HasTokens(value));
    }

    /// <summary>
    /// Property 2: WordLevel(a, b) is always in [0.0, 1.0].
    /// **Validates: Requirements 8.2**
    /// </summary>
    [Property(DisplayName = "Property 2: TextSimilarity bounds")]
    public bool Bounds_AlwaysInRange(string a, string b)
    {
        double similarity = TextSimilarity.WordLevel(a ?? "", b ?? "");
        return similarity >= 0.0 && similarity <= 1.0;
    }

    /// <summary>
    /// Property 3: WordLevel(a, b) == WordLevel(b, a) (LCS is symmetric).
    /// **Validates: Requirements 8.2**
    /// </summary>
    [Property(DisplayName = "Property 3: TextSimilarity symmetry")]
    public bool Symmetry_OrderDoesNotMatter(string a, string b)
    {
        double forward = TextSimilarity.WordLevel(a ?? "", b ?? "");
        double reverse = TextSimilarity.WordLevel(b ?? "", a ?? "");
        return Math.Abs(forward - reverse) < 1e-10;
    }

    private static bool HasTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(w => w.ToLowerInvariant().Trim(',', '.', '!', '?', ';', ':').Length > 0);
}
