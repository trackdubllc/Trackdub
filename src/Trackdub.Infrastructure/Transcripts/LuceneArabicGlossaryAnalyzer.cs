using Lucene.Net.Analysis.Ar;
using Lucene.Net.Util;

namespace Trackdub.Infrastructure.Transcripts;

public sealed class LuceneArabicGlossaryAnalyzer() : LuceneGlossaryAnalyzerBase(
    new ArabicAnalyzer(LuceneVersion.LUCENE_48),
    new HashSet<string>(StringComparer.Ordinal) { "ar" });
