using Trackdub.Contracts;
using Trackdub.Domain.Speakers;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteSpeakerConsentService(
    SqliteProjectDatabase database,
    IScopedConnectionProvider? scopedConnectionProvider = null)
    : ISpeakerConsentService
{
    private readonly SqliteProjectDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<bool> IsConsentGrantedAsync(Guid speakerId, CancellationToken cancellationToken)
    {
        VoiceCloneConsentRecord? record = await GetConsentAsync(speakerId, cancellationToken).ConfigureAwait(false);
        return record?.IsActive == true;
    }

    public async Task<VoiceCloneConsentRecord?> GetConsentAsync(Guid speakerId, CancellationToken cancellationToken)
    {
        if (!File.Exists(database.DatabasePath))
        {
            return null;
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, project_id, speaker_id, granted_at_utc, consent_version,
                   is_third_party, notes, expires_at_utc, revoked_at_utc
            FROM voice_clone_consents
            WHERE speaker_id = $speakerId
            ORDER BY granted_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$speakerId", SqliteValueConverters.ToDbValue(speakerId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    public async Task<VoiceCloneConsentRecord> RecordConsentAsync(
        Guid projectId,
        Guid speakerId,
        bool isThirdPartyConsent,
        string? notes,
        CancellationToken cancellationToken)
    {
        VoiceCloneConsentRecord record = VoiceCloneConsentRecord.Create(projectId, speakerId, isThirdPartyConsent, notes);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await EnsureSpeakerExistsAsync(speakerId, connection, cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO voice_clone_consents (
                id, project_id, speaker_id, granted_at_utc, consent_version,
                is_third_party, notes, expires_at_utc, revoked_at_utc)
            VALUES (
                $id, $projectId, $speakerId, $grantedAtUtc, $consentVersion,
                $isThirdParty, $notes, $expiresAtUtc, $revokedAtUtc);
            """;
        BindRecord(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task RevokeConsentAsync(Guid speakerId, CancellationToken cancellationToken)
    {
        if (!File.Exists(database.DatabasePath))
        {
            return;
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE voice_clone_consents
            SET revoked_at_utc = $revokedAtUtc
            WHERE speaker_id = $speakerId AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$speakerId", SqliteValueConverters.ToDbValue(speakerId));
        command.Parameters.AddWithValue("$revokedAtUtc", SqliteValueConverters.ToDbValue(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSpeakerExistsAsync(Guid speakerId, SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM speakers
            WHERE id = $speakerId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$speakerId", SqliteValueConverters.ToDbValue(speakerId));

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException($"Cannot record consent for missing speaker '{speakerId}'.");
        }
    }

    private static VoiceCloneConsentRecord ReadRecord(SqliteDataReader reader) =>
        new(
            SqliteValueConverters.ParseGuid(reader.GetString(0)),
            SqliteValueConverters.ParseGuid(reader.GetString(1)),
            SqliteValueConverters.ParseGuid(reader.GetString(2)),
            SqliteValueConverters.ParseDateTimeOffset(reader.GetString(3)),
            reader.GetString(4),
            reader.GetInt64(5) == 1,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : SqliteValueConverters.ParseDateTimeOffset(reader.GetString(7)),
            reader.IsDBNull(8) ? null : SqliteValueConverters.ParseDateTimeOffset(reader.GetString(8)));

    private static void BindRecord(SqliteCommand command, VoiceCloneConsentRecord record)
    {
        command.Parameters.AddWithValue("$id", SqliteValueConverters.ToDbValue(record.Id));
        command.Parameters.AddWithValue("$projectId", SqliteValueConverters.ToDbValue(record.ProjectId));
        command.Parameters.AddWithValue("$speakerId", SqliteValueConverters.ToDbValue(record.SpeakerId));
        command.Parameters.AddWithValue("$grantedAtUtc", SqliteValueConverters.ToDbValue(record.GrantedAtUtc));
        command.Parameters.AddWithValue("$consentVersion", record.ConsentVersion);
        command.Parameters.AddWithValue("$isThirdParty", record.IsThirdPartyConsent ? 1L : 0L);
        command.Parameters.AddWithValue("$notes", record.Notes is null ? DBNull.Value : record.Notes);
        command.Parameters.AddWithValue("$expiresAtUtc", record.ExpiresAtUtc is { } exp ? SqliteValueConverters.ToDbValue(exp) : DBNull.Value);
        command.Parameters.AddWithValue("$revokedAtUtc", record.RevokedAtUtc is { } rev ? SqliteValueConverters.ToDbValue(rev) : DBNull.Value);
    }

    private Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionLease.OpenAsync(database, scopedConnectionProvider, cancellationToken);
}
