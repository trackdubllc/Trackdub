using System.Text;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Transcripts;

public sealed class GlossaryService(
    IGlossaryRepository projectGlossaryRepository,
    IGlobalGlossaryRepository? globalGlossaryRepository = null)
{
    private readonly IGlossaryRepository projectGlossaryRepository = projectGlossaryRepository ?? throw new ArgumentNullException(nameof(projectGlossaryRepository));

    public Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken) =>
        GetProjectEntriesAsync(projectId, sourceLanguage, targetLanguage, cancellationToken);

    public Task SaveAsync(
        GlossaryEntry entry,
        CancellationToken cancellationToken) =>
        SaveAsync(entry, GlossaryStorageScope.Project, cancellationToken);

    public Task DeleteAsync(
        Guid projectId,
        Guid entryId,
        CancellationToken cancellationToken) =>
        DeleteAsync(projectId, entryId, GlossaryStorageScope.Project, cancellationToken);

    public Task<IReadOnlyList<GlossaryEntry>> GetProjectEntriesAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        GlossaryEntry.ValidateProjectScope(projectId);
        return projectGlossaryRepository.GetEntriesAsync(projectId, sourceLanguage, targetLanguage, cancellationToken);
    }

    public Task<IReadOnlyList<GlossaryEntry>> GetGlobalEntriesAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (globalGlossaryRepository is null)
        {
            return Task.FromResult<IReadOnlyList<GlossaryEntry>>([]);
        }

        return globalGlossaryRepository.GetEntriesAsync(sourceLanguage, targetLanguage, cancellationToken);
    }

    public async Task<IReadOnlyList<GlossaryEntry>> GetMergedEntriesAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        GlossaryEntry.ValidateProjectScope(projectId);

        IReadOnlyList<GlossaryEntry> projectEntries = await projectGlossaryRepository
            .GetEntriesAsync(projectId, sourceLanguage, targetLanguage, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<GlossaryEntry> globalEntries = globalGlossaryRepository is null
            ? []
            : await globalGlossaryRepository
                .GetEntriesAsync(sourceLanguage, targetLanguage, cancellationToken)
                .ConfigureAwait(false);

        var projectSourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (GlossaryEntry entry in projectEntries)
        {
            projectSourceKeys.Add(BuildMergeKey(entry));
        }

        var merged = new List<GlossaryEntry>(projectEntries.Count + globalEntries.Count);
        foreach (GlossaryEntry entry in globalEntries)
        {
            if (!projectSourceKeys.Contains(BuildMergeKey(entry)))
            {
                merged.Add(entry);
            }
        }

        merged.AddRange(projectEntries);

        return merged
            .OrderBy(entry => entry.SourceTerm, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetTerm, StringComparer.Ordinal)
            .ToArray();
    }

    public Task SaveAsync(
        GlossaryEntry entry,
        GlossaryStorageScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return scope switch
        {
            GlossaryStorageScope.Project when GlossaryScopeIds.IsGlobalScope(entry.ProjectId) =>
                throw new ArgumentException("Project-scoped saves cannot use the global scope id.", nameof(entry)),
            GlossaryStorageScope.Project => projectGlossaryRepository.SaveAsync(entry, cancellationToken),
            GlossaryStorageScope.Global when globalGlossaryRepository is null =>
                throw new InvalidOperationException("Global glossary storage is not configured."),
            GlossaryStorageScope.Global => SaveGlobalAsync(entry, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported glossary storage scope.")
        };
    }

    public Task DeleteAsync(
        Guid projectId,
        Guid entryId,
        GlossaryStorageScope scope,
        CancellationToken cancellationToken)
    {
        return scope switch
        {
            GlossaryStorageScope.Project => DeleteProjectEntryAsync(projectId, entryId, cancellationToken),
            GlossaryStorageScope.Global when globalGlossaryRepository is null =>
                throw new InvalidOperationException("Global glossary storage is not configured."),
            GlossaryStorageScope.Global => globalGlossaryRepository.DeleteAsync(entryId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported glossary storage scope.")
        };
    }

    public Task<IReadOnlyList<GlossaryEntry>> ImportCsvAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        Stream csvStream,
        bool isCaseSensitive,
        CancellationToken cancellationToken) =>
        ImportCsvAsync(
            projectId,
            sourceLanguage,
            targetLanguage,
            csvStream,
            isCaseSensitive,
            GlossaryStorageScope.Project,
            cancellationToken);

    public Task<IReadOnlyList<GlossaryConflict>> GetConflictsAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken) =>
        GetConflictsAsync(projectId, sourceLanguage, targetLanguage, GlossaryStorageScope.Project, cancellationToken);

    public Task<IReadOnlyList<GlossaryEntry>> ImportCsvAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        Stream csvStream,
        bool isCaseSensitive,
        GlossaryStorageScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(csvStream);

        return scope switch
        {
            GlossaryStorageScope.Project => ImportCsvInternalAsync(
                projectId,
                sourceLanguage,
                targetLanguage,
                csvStream,
                isCaseSensitive,
                createEntry: (sourceTerm, targetTerm, now) =>
                    GlossaryEntry.Create(projectId, sourceLanguage, targetLanguage, sourceTerm, targetTerm, isCaseSensitive, now),
                saveAsync: projectGlossaryRepository.SaveAsync,
                cancellationToken),
            GlossaryStorageScope.Global when globalGlossaryRepository is null =>
                throw new InvalidOperationException("Global glossary storage is not configured."),
            GlossaryStorageScope.Global => ImportCsvInternalAsync(
                GlossaryScopeIds.Global,
                sourceLanguage,
                targetLanguage,
                csvStream,
                isCaseSensitive,
                createEntry: (sourceTerm, targetTerm, now) =>
                    GlossaryEntry.CreateGlobal(sourceLanguage, targetLanguage, sourceTerm, targetTerm, isCaseSensitive, now),
                saveAsync: globalGlossaryRepository.SaveAsync,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported glossary storage scope.")
        };
    }

    public async Task<IReadOnlyList<GlossaryConflict>> GetConflictsAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        GlossaryStorageScope scope,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GlossaryEntry> entries = scope switch
        {
            GlossaryStorageScope.Project => await GetProjectEntriesAsync(projectId, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false),
            GlossaryStorageScope.Global => await GetGlobalEntriesAsync(sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported glossary storage scope.")
        };

        return entries
            .GroupBy(GetSourceTermLookupKey, StringComparer.Ordinal)
            .Select(group => new
            {
                NormalizedSourceTerm = group.Key,
                Entries = group.ToArray(),
                TargetTerms = group
                    .Select(entry => entry.TargetTerm)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            })
            .Where(group => group.TargetTerms.Length > 1)
            .Select(group => new GlossaryConflict(
                group.NormalizedSourceTerm,
                group.TargetTerms,
                group.Entries))
            .OrderBy(conflict => conflict.NormalizedSourceTerm, StringComparer.Ordinal)
            .ToArray();
    }

    private Task SaveGlobalAsync(GlossaryEntry entry, CancellationToken cancellationToken)
    {
        GlossaryEntry.ValidateGlobalScope(entry);
        return globalGlossaryRepository!.SaveAsync(entry, cancellationToken);
    }

    private Task DeleteProjectEntryAsync(
        Guid projectId,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        GlossaryEntry.ValidateProjectScope(projectId);
        return projectGlossaryRepository.DeleteAsync(projectId, entryId, cancellationToken);
    }

    private static async Task<IReadOnlyList<GlossaryEntry>> ImportCsvInternalAsync(
        Guid scopeProjectId,
        string sourceLanguage,
        string targetLanguage,
        Stream csvStream,
        bool isCaseSensitive,
        Func<string, string, DateTimeOffset, GlossaryEntry> createEntry,
        Func<GlossaryEntry, CancellationToken, Task> saveAsync,
        CancellationToken cancellationToken)
    {
        if (scopeProjectId != GlossaryScopeIds.Global)
        {
            GlossaryEntry.ValidateProjectScope(scopeProjectId);
        }

        var imported = new List<GlossaryEntry>();
        using var reader = new StreamReader(csvStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        int rowNumber = 0;
        bool hasSeenFirstNonEmptyRow = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            IReadOnlyList<string> fields = ParseCsvLine(line, rowNumber);
            if (fields.Count != 2)
            {
                throw new InvalidDataException($"Glossary CSV row {rowNumber} must contain exactly two columns.");
            }

            if (!hasSeenFirstNonEmptyRow && IsHeaderRow(fields))
            {
                hasSeenFirstNonEmptyRow = true;
                continue;
            }

            string sourceTerm = fields[0].Trim();
            string targetTerm = fields[1].Trim();
            if (string.IsNullOrWhiteSpace(sourceTerm) || string.IsNullOrWhiteSpace(targetTerm))
            {
                throw new InvalidDataException($"Glossary CSV row {rowNumber} contains an empty source or target term.");
            }

            GlossaryEntry entry = createEntry(sourceTerm, targetTerm, DateTimeOffset.UtcNow);
            await saveAsync(entry, cancellationToken).ConfigureAwait(false);
            imported.Add(entry);
            hasSeenFirstNonEmptyRow = true;
        }

        return imported;
    }

    internal static string GetSourceTermLookupKey(GlossaryEntry entry) =>
        entry.IsCaseSensitive ? entry.SourceTerm.Trim() : NormalizeSourceTerm(entry.SourceTerm);

    internal static string BuildMergeKey(GlossaryEntry entry) =>
        $"{entry.SourceLanguage}\0{entry.TargetLanguage}\0{GetSourceTermLookupKey(entry)}";

    private static bool IsHeaderRow(IReadOnlyList<string> fields)
    {
        string first = NormalizeHeader(fields[0]);
        string second = NormalizeHeader(fields[1]);
        return first is "source" or "source term" &&
               second is "target" or "target term";
    }

    private static string NormalizeHeader(string value) =>
        value.Trim().ToLowerInvariant();

    private static string NormalizeSourceTerm(string value) =>
        value.Trim().ToLowerInvariant();

    private static IReadOnlyList<string> ParseCsvLine(string line, int rowNumber)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (inQuotes)
        {
            throw new InvalidDataException($"Glossary CSV row {rowNumber} has an unterminated quoted field.");
        }

        fields.Add(current.ToString());
        return fields;
    }
}

public sealed record GlossaryConflict(
    string NormalizedSourceTerm,
    IReadOnlyList<string> TargetTerms,
    IReadOnlyList<GlossaryEntry> Entries);
