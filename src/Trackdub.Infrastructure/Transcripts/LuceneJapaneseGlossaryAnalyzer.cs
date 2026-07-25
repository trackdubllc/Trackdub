using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Ja;
using Lucene.Net.Analysis.Ja.TokenAttributes;
using Lucene.Net.Util;

namespace Trackdub.Infrastructure.Transcripts;

public sealed class LuceneJapaneseGlossaryAnalyzer() : LuceneGlossaryAnalyzerBase(
    new JapaneseAnalyzer(LuceneVersion.LUCENE_48),
    new HashSet<string>(StringComparer.Ordinal) { "ja" })
{
    private IBaseFormAttribute? baseFormAttribute;

    protected override void AddAnalyzerAttributes(TokenStream tokenStream)
    {
        baseFormAttribute = tokenStream.AddAttribute<IBaseFormAttribute>();
    }

    protected override string? GetLemma()
    {
        string? baseForm = baseFormAttribute?.GetBaseForm();
        return string.IsNullOrWhiteSpace(baseForm)
            ? null
            : baseForm;
    }
}
