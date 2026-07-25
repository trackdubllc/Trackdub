using Trackdub.Domain.Translation;

namespace Trackdub.Contracts.Transcripts;

public interface IGlossaryRepository
{
    Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);

    Task SaveAsync(
        GlossaryEntry entry,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid projectId,
        Guid entryId,
        CancellationToken cancellationToken);
}
