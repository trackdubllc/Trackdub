using Trackdub.Contracts.LipSynthesis;
using Trackdub.Domain.LipSynthesis;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteLipSynthesisSegmentRepository(SqliteProjectDatabase database)
    : ILipSynthesisSegmentRepository
{
    public async Task<IReadOnlyList<LipSynthesisSegment>> GetByProjectAsync(
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

    public async Task<IReadOnlyList<LipSynthesisSegment>> GetByStageRunAsync(
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
        IReadOnlyList<LipSynthesisSegment> segments,
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
            foreach (LipSynthesisSegment segment in segments)
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
        SELECT SegmentId,
               ProjectId,
               StageRunId,
               Status,
               SpeakerId,
               TurnStartSeconds,
               TurnEndSeconds,
               FaceConfidence,
               PatchedClipRelativePath,
               SkipReason,
               FailureReason,
               ProviderId,
               ModelId,
               UsedExperimentalProvider,
               CreatedAtUtc
        FROM LipSynthesisSegments
        """;

    private const string SelectByProjectSql =
        SelectColumns + " WHERE ProjectId = $projectId;";

    private const string SelectByStageRunSql =
        SelectColumns + " WHERE StageRunId = $stageRunId;";

    private const string InsertOrReplace =
        """
        INSERT OR REPLACE INTO LipSynthesisSegments (
            SegmentId,
            ProjectId,
            StageRunId,
            Status,
            SpeakerId,
            TurnStartSeconds,
            TurnEndSeconds,
            FaceConfidence,
            PatchedClipRelativePath,
            SkipReason,
            FailureReason,
            ProviderId,
            ModelId,
            UsedExperimentalProvider,
            CreatedAtUtc)
        VALUES (
            $segmentId,
            $projectId,
            $stageRunId,
            $status,
            $speakerId,
            $turnStartSeconds,
            $turnEndSeconds,
            $faceConfidence,
            $patchedClipRelativePath,
            $skipReason,
            $failureReason,
            $providerId,
            $modelId,
            $usedExperimentalProvider,
            $createdAtUtc);
        """;

    private static async Task<IReadOnlyList<LipSynthesisSegment>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<LipSynthesisSegment>();
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadSegment(reader));
        }

        return results;
    }

    private static LipSynthesisSegment ReadSegment(SqliteDataReader reader)
    {
        int colSegmentId = reader.GetOrdinal("SegmentId");
        int colStatus = reader.GetOrdinal("Status");
        int colSpeakerId = reader.GetOrdinal("SpeakerId");
        int colTurnStartSeconds = reader.GetOrdinal("TurnStartSeconds");
        int colTurnEndSeconds = reader.GetOrdinal("TurnEndSeconds");
        int colFaceConfidence = reader.GetOrdinal("FaceConfidence");
        int colPatchedClipRelativePath = reader.GetOrdinal("PatchedClipRelativePath");
        int colSkipReason = reader.GetOrdinal("SkipReason");
        int colFailureReason = reader.GetOrdinal("FailureReason");
        int colProviderId = reader.GetOrdinal("ProviderId");
        int colModelId = reader.GetOrdinal("ModelId");
        int colUsedExperimentalProvider = reader.GetOrdinal("UsedExperimentalProvider");
        int colCreatedAtUtc = reader.GetOrdinal("CreatedAtUtc");

        return new LipSynthesisSegment(
            SegmentId: Guid.Parse(reader.GetString(colSegmentId)),
            Status: Enum.Parse<LipSynthesisSegmentStatus>(reader.GetString(colStatus), ignoreCase: true),
            SpeakerId: reader.IsDBNull(colSpeakerId) ? null : reader.GetString(colSpeakerId),
            TurnStart: TimeSpan.FromSeconds(reader.GetDouble(colTurnStartSeconds)),
            TurnEnd: TimeSpan.FromSeconds(reader.GetDouble(colTurnEndSeconds)),
            FaceConfidence: reader.IsDBNull(colFaceConfidence) ? null : reader.GetDouble(colFaceConfidence),
            PatchedClipRelativePath: reader.IsDBNull(colPatchedClipRelativePath) ? null : reader.GetString(colPatchedClipRelativePath),
            SkipReason: reader.IsDBNull(colSkipReason) ? null : reader.GetString(colSkipReason),
            FailureReason: reader.IsDBNull(colFailureReason) ? null : reader.GetString(colFailureReason),
            ProviderId: reader.IsDBNull(colProviderId) ? null : reader.GetString(colProviderId),
            ModelId: reader.IsDBNull(colModelId) ? null : reader.GetString(colModelId),
            UsedExperimentalProvider: reader.GetInt64(colUsedExperimentalProvider) != 0,
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(colCreatedAtUtc)));
    }

    private static void BindSegment(
        SqliteCommand command,
        Guid projectId,
        Guid stageRunId,
        LipSynthesisSegment segment)
    {
        command.Parameters.AddWithValue("$segmentId", segment.SegmentId.ToString("D"));
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$stageRunId", stageRunId.ToString("D"));
        command.Parameters.AddWithValue("$status", segment.Status.ToString());
        command.Parameters.AddWithValue("$speakerId", segment.SpeakerId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$turnStartSeconds", segment.TurnStart.TotalSeconds);
        command.Parameters.AddWithValue("$turnEndSeconds", segment.TurnEnd.TotalSeconds);
        command.Parameters.AddWithValue("$faceConfidence", segment.FaceConfidence ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$patchedClipRelativePath", segment.PatchedClipRelativePath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$skipReason", segment.SkipReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$failureReason", segment.FailureReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$providerId", segment.ProviderId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$modelId", segment.ModelId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$usedExperimentalProvider", segment.UsedExperimentalProvider ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", segment.CreatedAtUtc.ToString("O"));
    }
}
