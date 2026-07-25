using Trackdub.Contracts;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Translation;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteGlossaryRepository(
    SqliteProjectDatabase database,
    IScopedConnectionProvider? scopedConnectionProvider = null)
    : IGlossaryRepository
{
    public async Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   source_language,
                   target_language,
                   source_term,
                   target_term,
                   is_case_sensitive,
                   created_at_utc,
                   updated_at_utc
            FROM glossary_entries
            WHERE project_id = $projectId
              AND source_language = $sourceLanguage
              AND target_language = $targetLanguage
            ORDER BY source_term, target_term;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$sourceLanguage", NormalizeLanguageCode(sourceLanguage, nameof(sourceLanguage)));
        command.Parameters.AddWithValue("$targetLanguage", NormalizeLanguageCode(targetLanguage, nameof(targetLanguage)));

        var results = new List<GlossaryEntry>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    public async Task SaveAsync(
        GlossaryEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO glossary_entries (
                id,
                project_id,
                source_language,
                target_language,
                source_term,
                target_term,
                is_case_sensitive,
                created_at_utc,
                updated_at_utc)
            VALUES (
                $id,
                $projectId,
                $sourceLanguage,
                $targetLanguage,
                $sourceTerm,
                $targetTerm,
                $isCaseSensitive,
                $createdAtUtc,
                $updatedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                project_id = excluded.project_id,
                source_language = excluded.source_language,
                target_language = excluded.target_language,
                source_term = excluded.source_term,
                target_term = excluded.target_term,
                is_case_sensitive = excluded.is_case_sensitive,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$projectId", entry.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$sourceLanguage", entry.SourceLanguage);
        command.Parameters.AddWithValue("$targetLanguage", entry.TargetLanguage);
        command.Parameters.AddWithValue("$sourceTerm", entry.SourceTerm);
        command.Parameters.AddWithValue("$targetTerm", entry.TargetTerm);
        command.Parameters.AddWithValue("$isCaseSensitive", entry.IsCaseSensitive ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", entry.CreatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("$updatedAtUtc", entry.UpdatedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        Guid projectId,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM glossary_entries
            WHERE project_id = $projectId
              AND id = $entryId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$entryId", entryId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static GlossaryEntry ReadEntry(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6) != 0,
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));

    private static string NormalizeLanguageCode(string? languageCode, string paramName)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            throw new ArgumentException("Language code is required.", paramName);
        }

        return GlossaryEntry.NormalizeLanguageCode(languageCode, paramName);
    }

    private Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionLease.OpenAsync(database, scopedConnectionProvider, cancellationToken);
}
