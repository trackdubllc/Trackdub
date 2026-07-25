using System.Globalization;
using Trackdub.Contracts.Transcripts;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;

namespace Trackdub.Infrastructure.Transcripts;

public abstract class LuceneGlossaryAnalyzerBase(
    Analyzer analyzer,
    IReadOnlySet<string> supportedSourceLanguages)
    : IGlossaryLanguageAnalyzer, IDisposable
{
    public IReadOnlySet<string> SupportedSourceLanguages { get; } = supportedSourceLanguages;

    public IReadOnlyList<GlossaryAnalysisToken> Analyze(
        string sourceLanguage,
        string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        int[] textElementCharIndexes = StringInfo.ParseCombiningCharacters(text);
        var tokens = new List<GlossaryAnalysisToken>();
        using var textReader = new StringReader(text);
        using TokenStream tokenStream = analyzer.GetTokenStream("glossary", textReader);
        ICharTermAttribute termAttribute = tokenStream.AddAttribute<ICharTermAttribute>();
        IOffsetAttribute offsetAttribute = tokenStream.AddAttribute<IOffsetAttribute>();
        AddAnalyzerAttributes(tokenStream);

        tokenStream.Reset();
        try
        {
            while (tokenStream.IncrementToken())
            {
                int startCharOffset = offsetAttribute.StartOffset;
                int endCharOffset = offsetAttribute.EndOffset;
                if (startCharOffset < 0 || endCharOffset <= startCharOffset || endCharOffset > text.Length)
                {
                    continue;
                }

                int startTextElementIndex = GetStartTextElementIndex(textElementCharIndexes, startCharOffset);
                int endTextElementIndex = GetEndTextElementIndex(
                    textElementCharIndexes,
                    endCharOffset,
                    text.Length);
                int textElementLength = endTextElementIndex - startTextElementIndex;
                if (startTextElementIndex < 0 || textElementLength <= 0)
                {
                    continue;
                }

                string surfaceText = text[startCharOffset..endCharOffset];
                string normalizedText = termAttribute.ToString();
                string? lemma = GetLemma();
                tokens.Add(new GlossaryAnalysisToken(
                    startTextElementIndex,
                    textElementLength,
                    surfaceText,
                    normalizedText,
                    lemma));
            }
        }
        finally
        {
            tokenStream.End();
        }

        return tokens;
    }

    public void Dispose()
    {
        analyzer.Dispose();
        GC.SuppressFinalize(this);
    }

    protected virtual void AddAnalyzerAttributes(TokenStream tokenStream)
    {
    }

    protected virtual string? GetLemma() => null;

    internal static int GetStartTextElementIndex(
        int[] textElementCharIndexes,
        int charOffset)
    {
        if (textElementCharIndexes.Length == 0)
        {
            return -1;
        }

        if (charOffset <= 0)
        {
            return 0;
        }

        int index = Array.BinarySearch(textElementCharIndexes, charOffset);
        if (index >= 0)
        {
            return index;
        }

        int nextTextElementIndex = ~index;
        return Math.Clamp(nextTextElementIndex - 1, 0, textElementCharIndexes.Length - 1);
    }

    internal static int GetEndTextElementIndex(
        int[] textElementCharIndexes,
        int charOffset,
        int textLength)
    {
        if (textElementCharIndexes.Length == 0)
        {
            return -1;
        }

        if (charOffset <= 0)
        {
            return 0;
        }

        if (charOffset >= textLength)
        {
            return textElementCharIndexes.Length;
        }

        int index = Array.BinarySearch(textElementCharIndexes, charOffset);
        if (index >= 0)
        {
            return index;
        }

        int nextTextElementIndex = ~index;
        return Math.Clamp(nextTextElementIndex, 0, textElementCharIndexes.Length);
    }
}
