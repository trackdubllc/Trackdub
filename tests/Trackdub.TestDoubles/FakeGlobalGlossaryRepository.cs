using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Translation;

namespace Trackdub.TestDoubles;

public sealed class FakeGlobalGlossaryRepository : IGlobalGlossaryRepository
{
    private readonly List<GlossaryEntry> _entries = [];

    public IReadOnlyList<GlossaryEntry> Entries => _entries;

    public Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GlossaryEntry> result = _entries
            .Where(entry =>
                string.Equals(entry.SourceLanguage, NormalizeLanguageCode(sourceLanguage), StringComparison.Ordinal) &&
                string.Equals(entry.TargetLanguage, NormalizeLanguageCode(targetLanguage), StringComparison.Ordinal))
            .OrderBy(entry => entry.SourceTerm, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task SaveAsync(GlossaryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int index = _entries.FindIndex(candidate => candidate.Id == entry.Id);
        if (index >= 0)
        {
            _entries[index] = entry;
        }
        else
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid entryId, CancellationToken cancellationToken)
    {
        _entries.RemoveAll(entry => entry.Id == entryId);
        return Task.CompletedTask;
    }

    private static string NormalizeLanguageCode(string languageCode) =>
        GlossaryEntry.NormalizeLanguageCode(languageCode, nameof(languageCode));
}
