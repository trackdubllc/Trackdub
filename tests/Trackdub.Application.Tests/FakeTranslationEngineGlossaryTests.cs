using Trackdub.Contracts.Pipeline;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class FakeTranslationEngineGlossaryTests
{
    [Fact]
    public async Task TranslateAsync_applies_case_insensitive_glossary_hints()
    {
        var engine = new FakeTranslationEngine(static (_, segment) => segment.Text);
        var request = new TranslationRequest(
            "en",
            "es",
            [new TranslationInputSegment(0, 0.0d, 1.0d, "hello HELLO")],
            GlossaryHints: [new TranslationGlossaryHint("hello", "hola", IsCaseSensitive: false)]);

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("hola hola", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task TranslateAsync_applies_case_sensitive_glossary_hints()
    {
        var engine = new FakeTranslationEngine(static (_, segment) => segment.Text);
        var request = new TranslationRequest(
            "en",
            "es",
            [new TranslationInputSegment(0, 0.0d, 1.0d, "CAT cat")],
            GlossaryHints: [new TranslationGlossaryHint("CAT", "gato", IsCaseSensitive: true)]);

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("gato cat", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task TranslateAsync_applies_span_backed_glossary_hints_to_matching_segment_only()
    {
        var engine = new FakeTranslationEngine(static (_, segment) => segment.Text);
        var request = new TranslationRequest(
            "ja",
            "en",
            [
                new TranslationInputSegment(0, 0.0d, 1.0d, "星宮先輩"),
                new TranslationInputSegment(1, 1.0d, 2.0d, "星宮先輩")
            ],
            GlossaryHints:
            [
                new TranslationGlossaryHint(
                    "星宮",
                    "Hoshimiya",
                    IsCaseSensitive: false,
                    SourceMatches: [new TranslationGlossarySourceMatch(0, 0, 2, "星宮")])
            ]);

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Collection(
            segments,
            segment => Assert.Equal("Hoshimiya先輩", segment.Text),
            segment => Assert.Equal("星宮先輩", segment.Text));
    }

    [Fact]
    public async Task TranslateAsync_applies_longest_match_span_replacement()
    {
        var engine = new FakeTranslationEngine(static (_, segment) => segment.Text);
        var request = new TranslationRequest(
            "ja",
            "en",
            [new TranslationInputSegment(0, 0.0d, 1.0d, "星宮先輩は来た")],
            GlossaryHints:
            [
                new TranslationGlossaryHint(
                    "星宮先輩",
                    "Hoshimiya-senpai",
                    IsCaseSensitive: false,
                    SourceMatches: [new TranslationGlossarySourceMatch(0, 0, 4, "星宮先輩")])
            ]);

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("Hoshimiya-senpaiは来た", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task TranslateAsync_uses_longest_overlapping_span_replacement()
    {
        var engine = new FakeTranslationEngine(static (_, segment) => segment.Text);
        var request = new TranslationRequest(
            "ja",
            "en",
            [new TranslationInputSegment(0, 0.0d, 1.0d, "星宮先輩")],
            GlossaryHints:
            [
                new TranslationGlossaryHint(
                    "星宮",
                    "Hoshimiya",
                    IsCaseSensitive: false,
                    SourceMatches: [new TranslationGlossarySourceMatch(0, 0, 2, "星宮")]),
                new TranslationGlossaryHint(
                    "星宮先輩",
                    "Hoshimiya-senpai",
                    IsCaseSensitive: false,
                    SourceMatches: [new TranslationGlossarySourceMatch(0, 0, 4, "星宮先輩")])
            ]);

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("Hoshimiya-senpai", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task TranslateAsync_applies_normalized_span_replacement_to_original_source_text()
    {
        var engine = new FakeTranslationEngine(static (_, segment) => segment.Text);
        var request = new TranslationRequest(
            "fr",
            "en",
            [new TranslationInputSegment(0, 0.0d, 1.0d, "Le Café ouvre.")],
            GlossaryHints:
            [
                new TranslationGlossaryHint(
                    "cafe",
                    "cafe glossary",
                    IsCaseSensitive: false,
                    SourceMatches: [new TranslationGlossarySourceMatch(0, 3, 4, "Café")])
            ]);

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("Le cafe glossary ouvre.", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task TranslateAsync_keeps_existing_output_without_glossary_hints()
    {
        var engine = new FakeTranslationEngine();
        var request = new TranslationRequest(
            "en",
            "es",
            [new TranslationInputSegment(0, 0.0d, 1.0d, "Hello")]);

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("Segmento generado 1.", Assert.Single(segments).Text);
    }
}
