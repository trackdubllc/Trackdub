using Trackdub.Domain.Translation;

namespace Trackdub.Contracts.Transcripts;

public interface IGlobalGlossaryRepository
{
    Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);

    Task SaveAsync(
        GlossaryEntry entry,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid entryId,
        CancellationToken cancellationToken);
}
