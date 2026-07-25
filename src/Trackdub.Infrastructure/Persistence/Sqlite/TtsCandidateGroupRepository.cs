using Trackdub.Contracts;
using Trackdub.Domain.Tts;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class TtsCandidateGroupRepository(
    SqliteProjectDatabase database,
    IScopedConnectionProvider? scopedConnectionProvider = null)
    : ITtsCandidateGroupRepository
{
    private readonly SqliteProjectDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<TtsCandidateGroup?> GetBySegmentAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        await database.InitializeAsync(ct).ConfigureAwait(false);
        await using var connectionLease = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = connectionLease.Connection;

        const string sql = @"
            SELECT id, project_id, translated_segment_id, segment_index, selected_candidate_id, created_at_utc
            FROM tts_candidate_groups 
            WHERE translated_segment_id = $translatedSegmentId
            LIMIT 1;";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$translatedSegmentId", translatedSegmentId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadGroup(reader)
            : null;
    }

    public async Task SaveAsync(TtsCandidateGroup group, CancellationToken ct)
    {
        await database.InitializeAsync(ct).ConfigureAwait(false);
        await using var connectionLease = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = connectionLease.Connection;

        const string sql = @"
            INSERT INTO tts_candidate_groups 
                (id, project_id, translated_segment_id, segment_index, selected_candidate_id, created_at_utc)
            VALUES 
                ($id, $projectId, $translatedSegmentId, $segmentIndex, $selectedCandidateId, $createdAtUtc)
            ON CONFLICT (translated_segment_id) 
            DO UPDATE SET 
                selected_candidate_id = $selectedCandidateId;";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", group.Id.ToString("D"));
        command.Parameters.AddWithValue("$projectId", group.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$translatedSegmentId", group.TranslatedSegmentId.ToString("D"));
        command.Parameters.AddWithValue("$segmentIndex", group.SegmentIndex);
        command.Parameters.AddWithValue("$selectedCandidateId", group.SelectedCandidateId.ToString("D"));
        command.Parameters.AddWithValue("$createdAtUtc", group.CreatedAtUtc.ToString("o"));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid groupId, CancellationToken ct)
    {
        await database.InitializeAsync(ct).ConfigureAwait(false);
        await using var connectionLease = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = connectionLease.Connection;

        const string sql = "DELETE FROM tts_candidate_groups WHERE id = $groupId;";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$groupId", groupId.ToString("D"));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TtsCandidateGroup>> GetByProjectAsync(Guid projectId, CancellationToken ct)
    {
        await database.InitializeAsync(ct).ConfigureAwait(false);
        await using var connectionLease = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = connectionLease.Connection;

        const string sql = @"
            SELECT id, project_id, translated_segment_id, segment_index, selected_candidate_id, created_at_utc
            FROM tts_candidate_groups
            WHERE project_id = $projectId
            ORDER BY segment_index;";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        var results = new List<TtsCandidateGroup>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadGroup(reader));
        }

        return results;
    }

    private Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken ct) =>
        SqliteConnectionLease.OpenAsync(database, scopedConnectionProvider, ct);

    private static TtsCandidateGroup ReadGroup(SqliteDataReader reader)
    {
        return new TtsCandidateGroup(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetInt32(3),
            Guid.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5)));
    }
}