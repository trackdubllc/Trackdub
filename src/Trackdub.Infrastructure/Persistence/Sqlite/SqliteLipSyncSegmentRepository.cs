using Trackdub.Contracts.LipSync;
using Trackdub.Domain.LipSync;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteLipSyncSegmentRepository(SqliteProjectDatabase database)
    : ILipSyncSegmentRepository
{
    public async Task<IReadOnlyList<LipSyncSegment>> GetByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await SqliteConnectionLease
            .OpenAsync(database, scopedConnectionProvider: null, cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connectionLease.Connection.CreateCommand();
        command.CommandText = SelectByProjectSql;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LipSyncSegment>> GetByStageRunAsync(
        Guid stageRunId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await SqliteConnectionLease
            .OpenAsync(database, scopedConnectionProvider: null, cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connectionLease.Connection.CreateCommand();
        command.CommandText = SelectByStageRunSql;
        command.Parameters.AddWithValue("$stageRunId", stageRunId.ToString("D"));
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAllAsync(
        Guid projectId,
        Guid stageRunId,
        IReadOnlyList<LipSyncSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
        {
            return;
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await SqliteConnectionLease
            .OpenAsync(database, scopedConnectionProvider: null, cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;

        if (segments.Count == 1)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = InsertOrReplace;
            BindSegment(command, projectId, stageRunId, segments[0]);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = InsertOrReplace;
            foreach (LipSyncSegment segment in segments)
            {
                command.Parameters.Clear();
                BindSegment(command, projectId, stageRunId, segment);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private const string SelectColumns =
        """
        SELECT Id,
               ProjectId,
               TranslatedSegmentId,
               StageRunId,
               Status,
               SourceAlignmentId,
               TtsAlignmentId,
               SourceDurationSeconds,
               TtsDurationSeconds,
               AlignedTtsDurationSeconds,
               PlanConfidence,
               SkipReason,
               FailureReason,
               ProviderId,
               ModelId,
               CreatedAtUtc
        FROM LipSyncSegments
        """;

    private const string SelectByProjectSql =
        SelectColumns + " WHERE ProjectId = $projectId;";

    private const string SelectByStageRunSql =
        SelectColumns + " WHERE StageRunId = $stageRunId;";

    private const string InsertOrReplace =
        """
        INSERT OR REPLACE INTO LipSyncSegments (
            Id,
            ProjectId,
            TranslatedSegmentId,
            StageRunId,
            Status,
            SourceAlignmentId,
            TtsAlignmentId,
            SourceDurationSeconds,
            TtsDurationSeconds,
            AlignedTtsDurationSeconds,
            PlanConfidence,
            SkipReason,
            FailureReason,
            ProviderId,
            ModelId,
            CreatedAtUtc)
        VALUES (
            $id,
            $projectId,
            $translatedSegmentId,
            $stageRunId,
            $status,
            $sourceAlignmentId,
            $ttsAlignmentId,
            $sourceDurationSeconds,
            $ttsDurationSeconds,
            $alignedTtsDurationSeconds,
            $planConfidence,
            $skipReason,
            $failureReason,
            $providerId,
            $modelId,
            $createdAtUtc);
        """;

    private static async Task<IReadOnlyList<LipSyncSegment>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<LipSyncSegment>();
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadSegment(reader));
        }

        return results;
    }

    private static LipSyncSegment ReadSegment(SqliteDataReader reader)
    {
        int colId = reader.GetOrdinal("Id");
        int colStatus = reader.GetOrdinal("Status");
        int colSourceAlignmentId = reader.GetOrdinal("SourceAlignmentId");
        int colTtsAlignmentId = reader.GetOrdinal("TtsAlignmentId");
        int colSourceDurationSeconds = reader.GetOrdinal("SourceDurationSeconds");
        int colTtsDurationSeconds = reader.GetOrdinal("TtsDurationSeconds");
        int colAlignedTtsDurationSeconds = reader.GetOrdinal("AlignedTtsDurationSeconds");
        int colPlanConfidence = reader.GetOrdinal("PlanConfidence");
        int colSkipReason = reader.GetOrdinal("SkipReason");
        int colFailureReason = reader.GetOrdinal("FailureReason");
        int colProviderId = reader.GetOrdinal("ProviderId");
        int colModelId = reader.GetOrdinal("ModelId");
        int colCreatedAtUtc = reader.GetOrdinal("CreatedAtUtc");

        return new LipSyncSegment(
            SegmentId: Guid.Parse(reader.GetString(colId)),
            Status: Enum.Parse<LipSyncSegmentStatus>(reader.GetString(colStatus), ignoreCase: true),
            SourceAlignmentId: reader.IsDBNull(colSourceAlignmentId) ? null : reader.GetString(colSourceAlignmentId),
            TtsAlignmentId: reader.IsDBNull(colTtsAlignmentId) ? null : reader.GetString(colTtsAlignmentId),
            SourceDuration: TimeSpan.FromSeconds(reader.GetDouble(colSourceDurationSeconds)),
            TtsDuration: TimeSpan.FromSeconds(reader.GetDouble(colTtsDurationSeconds)),
            AlignedTtsDuration: reader.IsDBNull(colAlignedTtsDurationSeconds)
                ? null
                : TimeSpan.FromSeconds(reader.GetDouble(colAlignedTtsDurationSeconds)),
            PlanConfidence: reader.IsDBNull(colPlanConfidence) ? null : reader.GetDouble(colPlanConfidence),
            SkipReason: reader.IsDBNull(colSkipReason) ? null : reader.GetString(colSkipReason),
            FailureReason: reader.IsDBNull(colFailureReason) ? null : reader.GetString(colFailureReason),
            ProviderId: reader.IsDBNull(colProviderId) ? null : reader.GetString(colProviderId),
            ModelId: reader.IsDBNull(colModelId) ? null : reader.GetString(colModelId),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(colCreatedAtUtc)));
    }

    private static void BindSegment(
        SqliteCommand command,
        Guid projectId,
        Guid stageRunId,
        LipSyncSegment segment)
    {
        command.Parameters.AddWithValue("$id", segment.SegmentId.ToString("D"));
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$translatedSegmentId", segment.SegmentId.ToString("D"));
        command.Parameters.AddWithValue("$stageRunId", stageRunId.ToString("D"));
        command.Parameters.AddWithValue("$status", segment.Status.ToString());
        command.Parameters.AddWithValue("$sourceAlignmentId",
            segment.SourceAlignmentId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$ttsAlignmentId",
            segment.TtsAlignmentId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$sourceDurationSeconds",
            segment.SourceDuration.TotalSeconds);
        command.Parameters.AddWithValue("$ttsDurationSeconds",
            segment.TtsDuration.TotalSeconds);
        command.Parameters.AddWithValue("$alignedTtsDurationSeconds",
            segment.AlignedTtsDuration.HasValue
                ? segment.AlignedTtsDuration.Value.TotalSeconds
                : (object)DBNull.Value);
        command.Parameters.AddWithValue("$planConfidence",
            segment.PlanConfidence ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$skipReason",
            segment.SkipReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$failureReason",
            segment.FailureReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$providerId",
            segment.ProviderId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$modelId",
            segment.ModelId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc",
            segment.CreatedAtUtc.ToString("O"));
    }
}
