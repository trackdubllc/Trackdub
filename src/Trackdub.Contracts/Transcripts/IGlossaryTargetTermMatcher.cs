using Trackdub.Domain.Translation;

namespace Trackdub.Contracts.Transcripts;

public interface IGlossaryTargetTermMatcher
{
    IReadOnlyList<GlossaryTextHighlightSpan> FindHighlightSpans(
        string targetLanguage,
        string translatedText,
        IReadOnlyList<GlossaryEntry> entries);
}
