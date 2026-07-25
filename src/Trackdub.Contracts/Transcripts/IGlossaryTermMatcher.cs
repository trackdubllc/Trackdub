using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Translation;

namespace Trackdub.Contracts.Transcripts;

public interface IGlossaryTermMatcher
{
    IReadOnlyList<TranslationGlossaryHint> BuildHints(
        string sourceLanguage,
        IReadOnlyList<TranslationInputSegment> segments,
        IReadOnlyList<GlossaryEntry> entries);
}
