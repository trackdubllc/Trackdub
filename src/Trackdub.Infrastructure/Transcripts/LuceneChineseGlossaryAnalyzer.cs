using Lucene.Net.Analysis.Cn.Smart;
using Lucene.Net.Util;

namespace Trackdub.Infrastructure.Transcripts;

public sealed class LuceneChineseGlossaryAnalyzer() : LuceneGlossaryAnalyzerBase(
    new SmartChineseAnalyzer(LuceneVersion.LUCENE_48),
    new HashSet<string>(StringComparer.Ordinal)
    {
        "zh",
        "zh-hans",
        "zh-hant"
    });
