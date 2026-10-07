using Trackdub.Contracts;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Tts;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteTtsTakeRepository(
    SqliteProjectDatabase database,
    IScopedConnectionProvider? scopedConnectionProvider = null)
    : ITtsTakeRepository
{
    public async Task<TtsTake?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectByIdSql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTake(reader)
            : null;
    }

    public async Task<IReadOnlyList<TtsTake>> GetByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectByProjectSql;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TtsTake?> GetByFingerprintAsync(
        Guid projectId,
        string inputFingerprint,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectByFingerprintSql;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$inputFingerprint", inputFingerprint);
        command.Parameters.AddWithValue("$status", TtsTakeStatus.Completed.ToString());

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTake(reader)
            : null;
    }

    public async Task<IReadOnlyList<TtsTake>> GetBySegmentAsync(
        Guid translatedSegmentId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectByTranslatedSegmentSql;
        command.Parameters.AddWithValue("$translatedSegmentId", translatedSegmentId.ToString("D"));
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TtsTake>> GetStaleBySpeakerAsync(
        Guid projectId,
        Guid voiceAssignmentId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectStaleBySpeakerSql;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$voiceAssignmentId", voiceAssignmentId.ToString("D"));
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkBySegmentIndicesStaleAsync(
        Guid projectId,
        IReadOnlySet<int> segmentIndices,
        CancellationToken cancellationToken)
    {
        if (segmentIndices.Count == 0)
        {
            return;
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE tts_takes
            SET is_stale = 1,
                status = $status,
                pre_stretch_duration_seconds = NULL,
                stretch_ratio_applied = NULL,
                stretch_mode = $stretchMode,
                stretch_engine = $stretchEngine
            WHERE project_id = $projectId
              AND segment_index = $segmentIndex;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$status", TtsTakeStatus.Stale.ToString());
        command.Parameters.AddWithValue("$stretchMode", TtsStretchMode.None.ToString());
        command.Parameters.AddWithValue("$stretchEngine", TtsStretchEngine.None.ToString());
        SqliteParameter segmentIndexParameter = command.Parameters.Add("$segmentIndex", SqliteType.Integer);
        foreach (int segmentIndex in segmentIndices)
        {
            segmentIndexParameter.Value = segmentIndex;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkByVoiceAssignmentStaleAsync(
        Guid projectId,
        Guid voiceAssignmentId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE tts_takes
            SET is_stale = 1,
                status = $status,
                pre_stretch_duration_seconds = NULL,
                stretch_ratio_applied = NULL,
                stretch_mode = $stretchMode,
                stretch_engine = $stretchEngine
            WHERE project_id = $projectId
              AND voice_assignment_id = $voiceAssignmentId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$voiceAssignmentId", voiceAssignmentId.ToString("D"));
        command.Parameters.AddWithValue("$status", TtsTakeStatus.Stale.ToString());
        command.Parameters.AddWithValue("$stretchMode", TtsStretchMode.None.ToString());
        command.Parameters.AddWithValue("$stretchEngine", TtsStretchEngine.None.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(TtsTake take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(take);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tts_takes (
                id,
                project_id,
                voice_assignment_id,
                translated_segment_id,
                segment_index,
                translated_text_hash,
                artifact_id,
                stage_run_id,
                status,
                is_stale,
                duration_samples,
                sample_rate,
                provider,
                model_id,
                voice_id,
                kind,
                reference_clip_artifact_id,
                duration_overrun_ratio,
                pre_stretch_duration_seconds,
                stretch_ratio_applied,
                stretch_mode,
                stretch_engine,
                created_at_utc,
                input_fingerprint,
                candidate_group_id,
                candidate_index,
                candidate_variant)
            VALUES (
                $id,
                $projectId,
                $voiceAssignmentId,
                $translatedSegmentId,
                $segmentIndex,
                $translatedTextHash,
                $artifactId,
                $stageRunId,
                $status,
                $isStale,
                $durationSamples,
                $sampleRate,
                $provider,
                $modelId,
                $voiceId,
                $kind,
                $referenceClipArtifactId,
                $durationOverrunRatio,
                $preStretchDurationSeconds,
                $stretchRatioApplied,
                $stretchMode,
                $stretchEngine,
                $createdAtUtc,
                $inputFingerprint,
                $candidateGroupId,
                $candidateIndex,
                $candidateVariant)
            ON CONFLICT(id) DO UPDATE SET
                artifact_id = excluded.artifact_id,
                stage_run_id = excluded.stage_run_id,
                status = excluded.status,
                is_stale = excluded.is_stale,
                duration_samples = excluded.duration_samples,
                sample_rate = excluded.sample_rate,
                provider = excluded.provider,
                model_id = excluded.model_id,
                voice_id = excluded.voice_id,
                kind = excluded.kind,
                reference_clip_artifact_id = excluded.reference_clip_artifact_id,
                duration_overrun_ratio = excluded.duration_overrun_ratio,
                pre_stretch_duration_seconds = excluded.pre_stretch_duration_seconds,
                stretch_ratio_applied = excluded.stretch_ratio_applied,
                stretch_mode = excluded.stretch_mode,
                stretch_engine = excluded.stretch_engine,
                input_fingerprint = excluded.input_fingerprint,
                candidate_group_id = excluded.candidate_group_id,
                candidate_index = excluded.candidate_index,
                candidate_variant = excluded.candidate_variant;
            """;
        BindTake(command, take);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SelectColumns =
        """
        SELECT id,
               project_id,
               voice_assignment_id,
               translated_segment_id,
               segment_index,
               translated_text_hash,
               artifact_id,
               stage_run_id,
               status,
               is_stale,
               duration_samples,
               sample_rate,
               provider,
               model_id,
               voice_id,
               kind,
               reference_clip_artifact_id,
               duration_overrun_ratio,
               pre_stretch_duration_seconds,
               stretch_ratio_applied,
               stretch_mode,
               stretch_engine,
               created_at_utc,
               input_fingerprint,
               candidate_group_id,
               candidate_index,
               candidate_variant
        FROM tts_takes
        """;

    private const string SelectByIdSql =
        SelectColumns + " WHERE id = $id LIMIT 1;";

    private const string SelectByProjectSql =
        SelectColumns + " WHERE project_id = $projectId ORDER BY created_at_utc, segment_index;";

    private const string SelectByFingerprintSql =
        SelectColumns + " WHERE project_id = $projectId" +
        " AND input_fingerprint = $inputFingerprint" +
        " AND is_stale = 0" +
        " AND status = $status" +
        " ORDER BY created_at_utc DESC LIMIT 1;";

    private const string SelectByTranslatedSegmentSql =
        SelectColumns + " WHERE translated_segment_id = $translatedSegmentId ORDER BY created_at_utc;";

    private const string SelectStaleBySpeakerSql =
        SelectColumns + " WHERE project_id = $projectId" +
        " AND voice_assignment_id = $voiceAssignmentId" +
        " AND is_stale = 1" +
        " ORDER BY segment_index, created_at_utc;";

    private static async Task<IReadOnlyList<TtsTake>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<TtsTake>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadTake(reader));
        }

        return results;
    }

    private static void BindTake(SqliteCommand command, TtsTake take)
    {
        command.Parameters.AddWithValue("$id", take.Id.ToString("D"));
        command.Parameters.AddWithValue("$projectId", take.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$voiceAssignmentId", take.VoiceAssignmentId.ToString("D"));
        command.Parameters.AddWithValue("$translatedSegmentId", take.TranslatedSegmentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$segmentIndex", take.SegmentIndex);
        command.Parameters.AddWithValue("$translatedTextHash", take.TranslatedTextHash ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$artifactId", take.ArtifactId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$stageRunId", take.StageRunId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", take.Status.ToString());
        command.Parameters.AddWithValue("$isStale", take.IsStale ? 1 : 0);
        command.Parameters.AddWithValue("$durationSamples", take.DurationSamples ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$sampleRate", take.SampleRate ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$provider", take.Provider ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$modelId", take.ModelId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$voiceId", take.VoiceId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$kind", take.Kind.ToString());
        command.Parameters.AddWithValue("$referenceClipArtifactId", take.ReferenceClipArtifactId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$durationOverrunRatio", take.DurationOverrunRatio ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$preStretchDurationSeconds", take.PreStretchDurationSeconds ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$stretchRatioApplied", take.StretchRatioApplied ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$stretchMode", take.StretchMode.ToString());
        command.Parameters.AddWithValue("$stretchEngine", take.StretchEngine.ToString());
        command.Parameters.AddWithValue("$createdAtUtc", take.CreatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("$inputFingerprint", take.InputFingerprint ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$candidateGroupId", take.CandidateGroupId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$candidateIndex", take.CandidateIndex ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$candidateVariant", (int)take.Variant);
    }

    private static TtsTake ReadTake(SqliteDataReader reader)
    {
        int colId = reader.GetOrdinal("id");
        int colProjectId = reader.GetOrdinal("project_id");
        int colVoiceAssignmentId = reader.GetOrdinal("voice_assignment_id");
        int colTranslatedSegmentId = reader.GetOrdinal("translated_segment_id");
        int colSegmentIndex = reader.GetOrdinal("segment_index");
        int colTranslatedTextHash = reader.GetOrdinal("translated_text_hash");
        int colArtifactId = reader.GetOrdinal("artifact_id");
        int colStageRunId = reader.GetOrdinal("stage_run_id");
        int colStatus = reader.GetOrdinal("status");
        int colIsStale = reader.GetOrdinal("is_stale");
        int colDurationSamples = reader.GetOrdinal("duration_samples");
        int colSampleRate = reader.GetOrdinal("sample_rate");
        int colProvider = reader.GetOrdinal("provider");
        int colModelId = reader.GetOrdinal("model_id");
        int colVoiceId = reader.GetOrdinal("voice_id");
        int colKind = reader.GetOrdinal("kind");
        int colReferenceClipArtifactId = reader.GetOrdinal("reference_clip_artifact_id");
        int colDurationOverrunRatio = reader.GetOrdinal("duration_overrun_ratio");
        int colPreStretchDurationSeconds = reader.GetOrdinal("pre_stretch_duration_seconds");
        int colStretchRatioApplied = reader.GetOrdinal("stretch_ratio_applied");
        int colStretchMode = reader.GetOrdinal("stretch_mode");
        int colStretchEngine = reader.GetOrdinal("stretch_engine");
        int colCreatedAtUtc = reader.GetOrdinal("created_at_utc");
        int colInputFingerprint = reader.GetOrdinal("input_fingerprint");
        int colCandidateGroupId = reader.GetOrdinal("candidate_group_id");
        int colCandidateIndex = reader.GetOrdinal("candidate_index");
        int colCandidateVariant = reader.GetOrdinal("candidate_variant");

        return new TtsTake(
            Guid.Parse(reader.GetString(colId)),
            Guid.Parse(reader.GetString(colProjectId)),
            Guid.Parse(reader.GetString(colVoiceAssignmentId)),
            reader.IsDBNull(colTranslatedSegmentId) ? null : Guid.Parse(reader.GetString(colTranslatedSegmentId)),
            reader.GetInt32(colSegmentIndex),
            reader.IsDBNull(colTranslatedTextHash) ? null : reader.GetString(colTranslatedTextHash),
            reader.IsDBNull(colArtifactId) ? null : Guid.Parse(reader.GetString(colArtifactId)),
            reader.IsDBNull(colStageRunId) ? null : Guid.Parse(reader.GetString(colStageRunId)),
            Enum.Parse<TtsTakeStatus>(reader.GetString(colStatus), ignoreCase: true),
            reader.GetInt64(colIsStale) == 1,
            reader.IsDBNull(colDurationSamples) ? null : reader.GetInt32(colDurationSamples),
            reader.IsDBNull(colSampleRate) ? null : reader.GetInt32(colSampleRate),
            reader.IsDBNull(colProvider) ? null : reader.GetString(colProvider),
            reader.IsDBNull(colModelId) ? null : reader.GetString(colModelId),
            reader.IsDBNull(colVoiceId) ? null : reader.GetString(colVoiceId),
            reader.IsDBNull(colKind) ? TtsTakeKind.Stock : Enum.Parse<TtsTakeKind>(reader.GetString(colKind), ignoreCase: true),
            reader.IsDBNull(colReferenceClipArtifactId) ? null : Guid.Parse(reader.GetString(colReferenceClipArtifactId)),
            reader.IsDBNull(colDurationOverrunRatio) ? null : reader.GetDouble(colDurationOverrunRatio),
            reader.IsDBNull(colPreStretchDurationSeconds) ? null : reader.GetDouble(colPreStretchDurationSeconds),
            reader.IsDBNull(colStretchRatioApplied) ? null : reader.GetDouble(colStretchRatioApplied),
            reader.IsDBNull(colStretchMode) ? TtsStretchMode.None : Enum.Parse<TtsStretchMode>(reader.GetString(colStretchMode), ignoreCase: true),
            reader.IsDBNull(colStretchEngine) ? TtsStretchEngine.None : Enum.Parse<TtsStretchEngine>(reader.GetString(colStretchEngine), ignoreCase: true),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(colCreatedAtUtc), DateTimeKind.Utc)),
            reader.IsDBNull(colInputFingerprint) ? null : reader.GetString(colInputFingerprint),
            reader.IsDBNull(colCandidateGroupId) ? null : Guid.Parse(reader.GetString(colCandidateGroupId)),
            reader.IsDBNull(colCandidateIndex) ? null : reader.GetInt32(colCandidateIndex),
            ReadCandidateVariant(reader, colCandidateVariant));
    }

    private static TtsCandidateVariant ReadCandidateVariant(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return TtsCandidateVariant.Primary;
        }

        object value = reader.GetValue(ordinal);
        return value switch
        {
            long numeric => (TtsCandidateVariant)(int)numeric,
            int numeric => (TtsCandidateVariant)numeric,
            string text when int.TryParse(text, out int numeric) => (TtsCandidateVariant)numeric,
            string text => Enum.Parse<TtsCandidateVariant>(text, ignoreCase: true),
            _ => TtsCandidateVariant.Primary
        };
    }

    private Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionLease.OpenAsync(database, scopedConnectionProvider, cancellationToken);
}
