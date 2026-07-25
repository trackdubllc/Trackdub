using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Tests;

public sealed class GlossaryTermMatcherTests
{
    [Fact]
    public void BuildHints_uses_registered_analyzer_tokens_before_fallback_scanner()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var analyzer = new FakeGlossaryLanguageAnalyzer(
            new HashSet<string>(StringComparer.Ordinal) { "ja" },
            new Dictionary<string, IReadOnlyList<GlossaryAnalysisToken>>(StringComparer.Ordinal)
            {
                ["星宮先輩"] =
                [
                    new GlossaryAnalysisToken(0, 4, "星宮先輩", "hoshimiya-senpai")
                ],
                ["hoshimiya-senpai"] =
                [
                    new GlossaryAnalysisToken(0, 16, "hoshimiya-senpai", "hoshimiya-senpai")
                ]
            });
        var matcher = new GlossaryTermMatcher(new GlossaryAnalyzerCatalog([analyzer]));
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "星宮先輩")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "hoshimiya-senpai", "Hoshimiya-senpai", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ja", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "hoshimiya-senpai",
            "Hoshimiya-senpai",
            segmentIndex: 0,
            startTextElementIndex: 0,
            textElementLength: 4,
            matchedSourceTerm: "星宮先輩");
    }

    [Fact]
    public void BuildHints_matches_analyzer_lemma_alternatives()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var analyzer = new FakeGlossaryLanguageAnalyzer(
            new HashSet<string>(StringComparer.Ordinal) { "ja" },
            new Dictionary<string, IReadOnlyList<GlossaryAnalysisToken>>(StringComparer.Ordinal)
            {
                ["食べた"] =
                [
                    new GlossaryAnalysisToken(0, 3, "食べた", "食べた", "食べる")
                ],
                ["食べる"] =
                [
                    new GlossaryAnalysisToken(0, 3, "食べる", "食べる")
                ]
            });
        var matcher = new GlossaryTermMatcher(new GlossaryAnalyzerCatalog([analyzer]));
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "食べた")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "食べる", "eat", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ja", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "食べる",
            "eat",
            segmentIndex: 0,
            startTextElementIndex: 0,
            textElementLength: 3,
            matchedSourceTerm: "食べた");
    }

    [Fact]
    public void BuildHints_preserves_analyzer_token_original_spans()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var analyzer = new FakeGlossaryLanguageAnalyzer(
            new HashSet<string>(StringComparer.Ordinal) { "ja" },
            new Dictionary<string, IReadOnlyList<GlossaryAnalysisToken>>(StringComparer.Ordinal)
            {
                ["前星宮後"] =
                [
                    new GlossaryAnalysisToken(1, 2, "星宮", "hoshimiya")
                ],
                ["hoshimiya"] =
                [
                    new GlossaryAnalysisToken(0, 9, "hoshimiya", "hoshimiya")
                ]
            });
        var matcher = new GlossaryTermMatcher(new GlossaryAnalyzerCatalog([analyzer]));
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "前星宮後")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "hoshimiya", "Hoshimiya", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ja", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "hoshimiya",
            "Hoshimiya",
            segmentIndex: 0,
            startTextElementIndex: 1,
            textElementLength: 2,
            matchedSourceTerm: "星宮");
    }

    [Fact]
    public void BuildHints_falls_back_to_morphology_lite_when_analyzer_fails()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher(new GlossaryAnalyzerCatalog(
            [new ThrowingGlossaryLanguageAnalyzer(new HashSet<string>(StringComparer.Ordinal) { "ja" })]));
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "ｶﾀｶﾅ")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "かたかな", "katakana glossary", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ja", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "かたかな",
            "katakana glossary",
            segmentIndex: 0,
            startTextElementIndex: 0,
            textElementLength: 4,
            matchedSourceTerm: "ｶﾀｶﾅ");
    }

    [Fact]
    public void BuildHints_skips_only_analyzer_candidate_that_fails_to_analyze()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var analyzer = new SelectivelyThrowingGlossaryLanguageAnalyzer(
            new HashSet<string>(StringComparer.Ordinal) { "ja" },
            new HashSet<string>(StringComparer.Ordinal) { "壊れた" },
            new Dictionary<string, IReadOnlyList<GlossaryAnalysisToken>>(StringComparer.Ordinal)
            {
                ["星宮"] =
                [
                    new GlossaryAnalysisToken(0, 2, "星宮", "hoshimiya")
                ],
                ["hoshimiya"] =
                [
                    new GlossaryAnalysisToken(0, 9, "hoshimiya", "hoshimiya")
                ]
            });
        var matcher = new GlossaryTermMatcher(new GlossaryAnalyzerCatalog([analyzer]));
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "星宮")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "壊れた", "broken", false, now),
            GlossaryEntry.Create(projectId, "ja", "en", "hoshimiya", "Hoshimiya", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ja", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "hoshimiya",
            "Hoshimiya",
            segmentIndex: 0,
            startTextElementIndex: 0,
            textElementLength: 2,
            matchedSourceTerm: "星宮");
    }

    [Fact]
    public void BuildHints_uses_scanner_for_entries_the_analyzer_does_not_match()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var analyzer = new FakeGlossaryLanguageAnalyzer(
            new HashSet<string>(StringComparer.Ordinal) { "ja" },
            new Dictionary<string, IReadOnlyList<GlossaryAnalysisToken>>(StringComparer.Ordinal)
            {
                ["星宮ｶﾀｶﾅ"] =
                [
                    new GlossaryAnalysisToken(0, 2, "星宮", "hoshimiya"),
                    new GlossaryAnalysisToken(2, 4, "ｶﾀｶﾅ", "tokenizer-only")
                ],
                ["hoshimiya"] =
                [
                    new GlossaryAnalysisToken(0, 9, "hoshimiya", "hoshimiya")
                ]
            });
        var matcher = new GlossaryTermMatcher(new GlossaryAnalyzerCatalog([analyzer]));
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "星宮ｶﾀｶﾅ")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "hoshimiya", "Hoshimiya", false, now),
            GlossaryEntry.Create(projectId, "ja", "en", "かたかな", "katakana glossary", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ja", segments, entries);

        Assert.Collection(
            hints.OrderBy(hint => hint.SourceTerm, StringComparer.Ordinal),
            hint => AssertMatch(
                hint,
                "hoshimiya",
                "Hoshimiya",
                segmentIndex: 0,
                startTextElementIndex: 0,
                textElementLength: 2,
                matchedSourceTerm: "星宮"),
            hint => AssertMatch(
                hint,
                "かたかな",
                "katakana glossary",
                segmentIndex: 0,
                startTextElementIndex: 2,
                textElementLength: 4,
                matchedSourceTerm: "ｶﾀｶﾅ"));
    }

    [Fact]
    public void BuildHints_matches_japanese_terms_in_unspaced_text()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "星宮先輩は魔導炉を見た")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "星宮", "Hoshimiya", false, now),
            GlossaryEntry.Create(projectId, "ja", "en", "先輩", "senpai", false, now),
            GlossaryEntry.Create(projectId, "ja", "en", "魔導炉", "magic reactor", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ja", segments, entries);

        Assert.Collection(
            hints.OrderBy(hint => hint.SourceTerm, StringComparer.Ordinal),
            hint => AssertMatch(hint, "先輩", "senpai", segmentIndex: 0, startTextElementIndex: 2, textElementLength: 2),
            hint => AssertMatch(hint, "星宮", "Hoshimiya", segmentIndex: 0, startTextElementIndex: 0, textElementLength: 2),
            hint => AssertMatch(hint, "魔導炉", "magic reactor", segmentIndex: 0, startTextElementIndex: 5, textElementLength: 3));
    }

    [Fact]
    public void BuildHints_prefers_longest_overlapping_japanese_match()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "星宮先輩は来た")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "星宮", "Hoshimiya", false, now),
            GlossaryEntry.Create(projectId, "ja", "en", "星宮先輩", "Hoshimiya-senpai", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ja", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(hint, "星宮先輩", "Hoshimiya-senpai", segmentIndex: 0, startTextElementIndex: 0, textElementLength: 4);
    }

    [Fact]
    public void BuildHints_matches_chinese_terms_inside_unspaced_text()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "我喜欢魔导炉")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "zh-Hans", "en", "魔导炉", "magic reactor", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("zh-Hans", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(hint, "魔导炉", "magic reactor", segmentIndex: 0, startTextElementIndex: 3, textElementLength: 3);
    }

    [Fact]
    public void BuildHints_matches_korean_terms_adjacent_to_hangul()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "하늘선배님이왔다")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ko", "en", "선배", "senbae", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ko", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(hint, "선배", "senbae", segmentIndex: 0, startTextElementIndex: 2, textElementLength: 2);
    }

    [Fact]
    public void BuildHints_for_non_cjk_source_preserves_basic_hints_without_spans()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "No literal match here.")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ru", "es", "server", "servidor", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ru", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        Assert.Equal("server", hint.SourceTerm);
        Assert.Equal("servidor", hint.TargetTerm);
        Assert.False(hint.IsCaseSensitive);
        Assert.Null(hint.SourceMatches);
    }

    [Fact]
    public void BuildHints_latin_accent_folding_matches_original_span()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "Le Café ouvre.")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "fr", "en", "cafe", "cafe glossary", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("fr", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "cafe",
            "cafe glossary",
            segmentIndex: 0,
            startTextElementIndex: 3,
            textElementLength: 4,
            matchedSourceTerm: "Café");
    }

    [Fact]
    public void BuildHints_latin_punctuation_variants_match_original_spans()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "Jean–Luc l’amour")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "fr", "en", "Jean-Luc", "Jean-Luc glossary", false, now),
            GlossaryEntry.Create(projectId, "fr", "en", "l'amour", "love glossary", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("fr", segments, entries);

        Assert.Collection(
            hints.OrderBy(hint => hint.SourceTerm, StringComparer.Ordinal),
            hint => AssertMatch(
                hint,
                "Jean-Luc",
                "Jean-Luc glossary",
                segmentIndex: 0,
                startTextElementIndex: 0,
                textElementLength: 8,
                matchedSourceTerm: "Jean–Luc"),
            hint => AssertMatch(
                hint,
                "l'amour",
                "love glossary",
                segmentIndex: 0,
                startTextElementIndex: 9,
                textElementLength: 7,
                matchedSourceTerm: "l’amour"));
    }

    [Fact]
    public void BuildHints_latin_case_sensitive_entries_do_not_match_different_casing()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "café")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "fr", "en", "Cafe", "cafe glossary", true, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("fr", segments, entries);

        Assert.Empty(hints);
    }

    [Fact]
    public void BuildHints_japanese_width_and_kana_variants_match_original_span()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "ｶﾀｶﾅとひらがな")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ja", "en", "かたかな", "katakana glossary", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ja", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "かたかな",
            "katakana glossary",
            segmentIndex: 0,
            startTextElementIndex: 0,
            textElementLength: 4,
            matchedSourceTerm: "ｶﾀｶﾅ");
    }

    [Fact]
    public void BuildHints_chinese_width_variants_match_original_span()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "Ａ计划启动")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "zh-Hans", "en", "A计划", "Project A", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("zh-Hans", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "A计划",
            "Project A",
            segmentIndex: 0,
            startTextElementIndex: 0,
            textElementLength: 3,
            matchedSourceTerm: "Ａ计划");
    }

    [Fact]
    public void BuildHints_arabic_diacritics_match_original_span()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "كِتاب")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ar", "en", "كتاب", "book", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ar", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "كتاب",
            "book",
            segmentIndex: 0,
            startTextElementIndex: 0,
            textElementLength: 4,
            matchedSourceTerm: "كِتاب");
    }

    [Fact]
    public void BuildHints_arabic_article_match_excludes_leading_connector()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "والكِتاب")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ar", "en", "الكتاب", "the book", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ar", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "الكتاب",
            "the book",
            segmentIndex: 0,
            startTextElementIndex: 1,
            textElementLength: 6,
            matchedSourceTerm: "الكِتاب");
    }

    [Fact]
    public void BuildHints_arabic_alef_variants_normalize()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matcher = new GlossaryTermMatcher();
        TranslationInputSegment[] segments =
        [
            new(0, 0.0d, 1.0d, "إيمان")
        ];
        GlossaryEntry[] entries =
        [
            GlossaryEntry.Create(projectId, "ar", "en", "ايمان", "Iman", false, now)
        ];

        IReadOnlyList<TranslationGlossaryHint> hints = matcher.BuildHints("ar", segments, entries);

        TranslationGlossaryHint hint = Assert.Single(hints);
        AssertMatch(
            hint,
            "ايمان",
            "Iman",
            segmentIndex: 0,
            startTextElementIndex: 0,
            textElementLength: 5,
            matchedSourceTerm: "إيمان");
    }

    private static void AssertMatch(
        TranslationGlossaryHint hint,
        string sourceTerm,
        string targetTerm,
        int segmentIndex,
        int startTextElementIndex,
        int textElementLength,
        string? matchedSourceTerm = null)
    {
        Assert.Equal(sourceTerm, hint.SourceTerm);
        Assert.Equal(targetTerm, hint.TargetTerm);
        TranslationGlossarySourceMatch match = Assert.Single(hint.SourceMatches ?? []);
        Assert.Equal(segmentIndex, match.SegmentIndex);
        Assert.Equal(startTextElementIndex, match.StartTextElementIndex);
        Assert.Equal(textElementLength, match.TextElementLength);
        Assert.Equal(matchedSourceTerm ?? sourceTerm, match.MatchedSourceTerm);
    }

    private sealed class FakeGlossaryLanguageAnalyzer(
        IReadOnlySet<string> supportedSourceLanguages,
        IReadOnlyDictionary<string, IReadOnlyList<GlossaryAnalysisToken>> tokensByText)
        : IGlossaryLanguageAnalyzer
    {
        public IReadOnlySet<string> SupportedSourceLanguages { get; } = supportedSourceLanguages;

        public IReadOnlyList<GlossaryAnalysisToken> Analyze(
            string sourceLanguage,
            string text)
        {
            Assert.Contains(sourceLanguage.Trim().ToLowerInvariant(), SupportedSourceLanguages);
            return tokensByText.TryGetValue(text, out IReadOnlyList<GlossaryAnalysisToken>? tokens)
                ? tokens
                : [];
        }
    }

    private sealed class ThrowingGlossaryLanguageAnalyzer(IReadOnlySet<string> supportedSourceLanguages)
        : IGlossaryLanguageAnalyzer
    {
        public IReadOnlySet<string> SupportedSourceLanguages { get; } = supportedSourceLanguages;

        public IReadOnlyList<GlossaryAnalysisToken> Analyze(
            string sourceLanguage,
            string text) =>
            throw new InvalidOperationException($"Analyzer failed for {sourceLanguage}:{text}.");
    }

    private sealed class SelectivelyThrowingGlossaryLanguageAnalyzer(
        IReadOnlySet<string> supportedSourceLanguages,
        IReadOnlySet<string> throwingTexts,
        IReadOnlyDictionary<string, IReadOnlyList<GlossaryAnalysisToken>> tokensByText)
        : IGlossaryLanguageAnalyzer
    {
        public IReadOnlySet<string> SupportedSourceLanguages { get; } = supportedSourceLanguages;

        public IReadOnlyList<GlossaryAnalysisToken> Analyze(
            string sourceLanguage,
            string text)
        {
            if (throwingTexts.Contains(text))
            {
                throw new InvalidOperationException($"Analyzer failed for {sourceLanguage}:{text}.");
            }

            return tokensByText.TryGetValue(text, out IReadOnlyList<GlossaryAnalysisToken>? tokens)
                ? tokens
                : [];
        }
    }
}
