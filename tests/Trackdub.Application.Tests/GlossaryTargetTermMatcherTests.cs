using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Tests;

public sealed class GlossaryTargetTermMatcherTests
{
    private readonly GlossaryTargetTermMatcher matcher = new();

    [Fact]
    public void FindHighlightSpans_matches_english_target_term_in_translation()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "hoshimiya", "Hoshimiya", false, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans(
            "en",
            "Meet Hoshimiya at the reactor.",
            entries);

        GlossaryTextHighlightSpan span = Assert.Single(spans);
        Assert.Equal(5, span.Start);
        Assert.Equal(9, span.Length);
    }

    [Fact]
    public void FindHighlightSpans_prefers_longest_overlapping_target_match()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "senpai", "senpai", false, now),
            GlossaryEntry.Create(projectId, "ja", "en", "hoshimiya-senpai", "Hoshimiya-senpai", false, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans(
            "en",
            "Hoshimiya-senpai arrived.",
            entries);

        GlossaryTextHighlightSpan span = Assert.Single(spans);
        Assert.Equal(0, span.Start);
        Assert.Equal(16, span.Length);
    }

    [Fact]
    public void FindHighlightSpans_latin_accent_folding_matches_original_span()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "fr", "en", "cafe", "cafe", false, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans(
            "en",
            "Stop at the Cafe first.",
            entries);

        GlossaryTextHighlightSpan span = Assert.Single(spans);
        Assert.Equal("Cafe", "Stop at the Cafe first.".Substring(span.Start, span.Length));
    }

    [Fact]
    public void FindHighlightSpans_case_sensitive_entries_skip_different_casing()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "cafe", "Cafe", true, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans(
            "en",
            "cafe",
            entries);

        Assert.Empty(spans);
    }

    [Fact]
    public void FindHighlightSpans_matches_japanese_target_terms_in_unspaced_text()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "en", "ja", "Hoshimiya", "星宮", false, now),
            GlossaryEntry.Create(projectId, "en", "ja", "senpai", "先輩", false, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans(
            "ja",
            "星宮先輩は来た",
            entries);

        Assert.Collection(
            spans.OrderBy(span => span.Start),
            span => Assert.Equal(0, span.Start),
            span => Assert.Equal(2, span.Start));
    }

    [Fact]
    public void FindHighlightSpans_returns_empty_for_blank_translation()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "hoshimiya", "Hoshimiya", false, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans("en", "   ", entries);

        Assert.Empty(spans);
    }

    [Fact]
    public void FindHighlightSpans_throws_for_null_entries()
    {
        Assert.Throws<ArgumentNullException>(() =>
            matcher.FindHighlightSpans("en", "hello", null!));
    }

    [Fact]
    public void FindHighlightSpans_returns_empty_for_empty_entries()
    {
        IReadOnlyList<GlossaryTextHighlightSpan> spans =
            matcher.FindHighlightSpans("en", "hello", []);

        Assert.Empty(spans);
    }

    [Fact]
    public void FindHighlightSpans_returns_empty_for_null_translation()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "hoshimiya", "Hoshimiya", false, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans =
            matcher.FindHighlightSpans("en", null!, entries);

        Assert.Empty(spans);
    }

    [Fact]
    public void FindHighlightSpans_does_not_emit_overlapping_shorter_term_inside_longer_match()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "hoshimiya", "Hoshimiya", false, now),
            GlossaryEntry.Create(projectId, "ja", "en", "shimi", "shimi", false, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans(
            "en",
            "Meet Hoshimiya today.",
            entries);

        GlossaryTextHighlightSpan span = Assert.Single(spans);
        Assert.Equal("Hoshimiya", "Meet Hoshimiya today.".Substring(span.Start, span.Length));
    }

    [Fact]
    public void FindHighlightSpans_maps_emoji_target_to_original_character_span()
    {
        const string text = "Wave 👋 there";
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "en", "en", "wave", "👋", false, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans("en", text, entries);

        GlossaryTextHighlightSpan span = Assert.Single(spans);
        Assert.Equal("👋", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void FindHighlightSpans_skips_target_terms_that_normalize_to_empty()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ar", "ar", "mark", "\u064B", false, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans(
            "ar",
            "\u064B\u0627\u0644\u0645",
            entries);

        Assert.Empty(spans);
    }

    [Fact]
    public void FindHighlightSpans_case_sensitive_entry_matches_exact_casing_after_normalization()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "fr", "en", "cafe", "Cafe", true, now)
        ];

        IReadOnlyList<GlossaryTextHighlightSpan> spans = matcher.FindHighlightSpans(
            "en",
            "Stop at the Cafe first.",
            entries);

        GlossaryTextHighlightSpan span = Assert.Single(spans);
        Assert.Equal("Cafe", "Stop at the Cafe first.".Substring(span.Start, span.Length));
    }
}
