using System.Globalization;
using Trackdub.Application.Transcripts;
using Trackdub.Infrastructure.Transcripts;

namespace Trackdub.Infrastructure.Tests;

public sealed class LuceneGlossaryLanguageAnalyzerTests
{
    [Fact]
    public void Japanese_analyzer_tokenizes_text_and_emits_original_spans()
    {
        using var analyzer = new LuceneJapaneseGlossaryAnalyzer();

        IReadOnlyList<GlossaryAnalysisToken> tokens = analyzer.Analyze("ja", "寿司を食べる");

        Assert.NotEmpty(tokens);
        Assert.Contains(tokens, token => token.SurfaceText == "寿司");
        Assert.All(tokens, AssertValidToken);
    }

    [Fact]
    public void Japanese_analyzer_exposes_base_form_when_lucene_emits_it()
    {
        using var analyzer = new LuceneJapaneseGlossaryAnalyzer();

        IReadOnlyList<GlossaryAnalysisToken> tokens = analyzer.Analyze("ja", "食べました");

        Assert.Contains(tokens, token => token.Lemma == "食べる");
    }

    [Fact]
    public void Chinese_analyzer_tokenizes_simplified_chinese_words()
    {
        using var analyzer = new LuceneChineseGlossaryAnalyzer();

        IReadOnlyList<GlossaryAnalysisToken> tokens = analyzer.Analyze("zh-Hans", "我是中国人");

        Assert.Contains(tokens, token => token.SurfaceText == "中国");
        Assert.All(tokens, AssertValidToken);
    }

    [Fact]
    public void Arabic_analyzer_normalizes_and_light_stems_terms()
    {
        using var analyzer = new LuceneArabicGlossaryAnalyzer();

        IReadOnlyList<GlossaryAnalysisToken> tokens = analyzer.Analyze("ar", "والكِتاب");

        Assert.Contains(tokens, token => token.NormalizedText == "كتاب");
        Assert.All(tokens, AssertValidToken);
    }

    [Fact]
    public void Analyzer_adapters_are_disposable()
    {
        using var japaneseAnalyzer = new LuceneJapaneseGlossaryAnalyzer();
        using var chineseAnalyzer = new LuceneChineseGlossaryAnalyzer();
        using var arabicAnalyzer = new LuceneArabicGlossaryAnalyzer();

        Assert.IsAssignableFrom<IDisposable>(japaneseAnalyzer);
        Assert.IsAssignableFrom<IDisposable>(chineseAnalyzer);
        Assert.IsAssignableFrom<IDisposable>(arabicAnalyzer);
    }

    [Fact]
    public void Text_element_mapping_floors_start_offsets_and_ceils_end_offsets_inside_clusters()
    {
        string text = "cafe\u0301!";
        int[] textElementCharIndexes = StringInfo.ParseCombiningCharacters(text);

        int startTextElementIndex = LuceneGlossaryAnalyzerBase.GetStartTextElementIndex(
            textElementCharIndexes,
            charOffset: 4);
        int endTextElementIndex = LuceneGlossaryAnalyzerBase.GetEndTextElementIndex(
            textElementCharIndexes,
            charOffset: 5,
            textLength: text.Length);

        Assert.Equal(3, startTextElementIndex);
        Assert.Equal(4, endTextElementIndex);
    }

    private static void AssertValidToken(GlossaryAnalysisToken token)
    {
        Assert.True(token.StartTextElementIndex >= 0);
        Assert.True(token.TextElementLength > 0);
        Assert.False(string.IsNullOrWhiteSpace(token.SurfaceText));
        Assert.False(string.IsNullOrWhiteSpace(token.NormalizedText));
    }
}
