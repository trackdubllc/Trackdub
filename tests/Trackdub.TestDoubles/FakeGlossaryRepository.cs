using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Translation;

namespace Trackdub.TestDoubles;

public sealed class FakeGlossaryRepository : IGlossaryRepository
{
    private readonly List<GlossaryEntry> entries = [];

    public IReadOnlyList<GlossaryEntry> Entries => entries;

    public Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GlossaryEntry> result = entries
            .Where(entry => entry.ProjectId == projectId &&
                            string.Equals(entry.SourceLanguage, NormalizeLanguageCode(sourceLanguage), StringComparison.Ordinal) &&
                            string.Equals(entry.TargetLanguage, NormalizeLanguageCode(targetLanguage), StringComparison.Ordinal))
            .OrderBy(entry => entry.SourceTerm, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task SaveAsync(GlossaryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        int index = entries.FindIndex(candidate => candidate.Id == entry.Id);
        if (index >= 0)
        {
            entries[index] = entry;
        }
        else
        {
            entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid projectId, Guid entryId, CancellationToken cancellationToken)
    {
        entries.RemoveAll(entry => entry.ProjectId == projectId && entry.Id == entryId);
        return Task.CompletedTask;
    }

    private static string NormalizeLanguageCode(string languageCode) =>
        GlossaryEntry.NormalizeLanguageCode(languageCode, nameof(languageCode));
}
