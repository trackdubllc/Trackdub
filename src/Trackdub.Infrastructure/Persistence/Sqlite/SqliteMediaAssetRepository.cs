using Trackdub.Contracts;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteMediaAssetRepository(
    SqliteProjectDatabase database,
    IScopedConnectionProvider? scopedConnectionProvider = null)
    : IMediaAssetRepository
{
    public async Task SaveAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO media_assets (
                id,
                project_id,
                source_file_path,
                source_file_name,
                fingerprint_sha256,
                source_size_bytes,
                source_last_write_time_utc,
                format_name,
                duration_seconds,
                has_audio,
                has_video,
                created_at_utc)
            VALUES (
                $id,
                $projectId,
                $sourceFilePath,
                $sourceFileName,
                $fingerprintSha256,
                $sourceSizeBytes,
                $sourceLastWriteTimeUtc,
                $formatName,
                $durationSeconds,
                $hasAudio,
                $hasVideo,
                $createdAtUtc);
            """;
        BindMediaAsset(command, asset);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSourcePathAsync(
        Guid mediaAssetId,
        string sourceFilePath,
        string sourceFileName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE media_assets
            SET source_file_path = $sourceFilePath,
                source_file_name = $sourceFileName
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", mediaAssetId.ToString("D"));
        command.Parameters.AddWithValue("$sourceFilePath", sourceFilePath);
        command.Parameters.AddWithValue("$sourceFileName", sourceFileName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaAsset?> GetPrimaryAsync(Guid projectId, CancellationToken cancellationToken)
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
            SELECT id,
                    project_id,
                    source_file_path,
                    source_file_name,
                    fingerprint_sha256,
                    source_size_bytes,
                    source_last_write_time_utc,
                    format_name,
                    duration_seconds,
                    has_audio,
                    has_video,
                    created_at_utc
            FROM media_assets
            WHERE project_id = $projectId
            ORDER BY created_at_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadMediaAsset(reader);
    }

    public async Task<IReadOnlyList<MediaAsset>> GetAllAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!File.Exists(database.DatabasePath))
        {
            return [];
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                    project_id,
                    source_file_path,
                    source_file_name,
                    fingerprint_sha256,
                    source_size_bytes,
                    source_last_write_time_utc,
                    format_name,
                    duration_seconds,
                    has_audio,
                    has_video,
                    created_at_utc
            FROM media_assets
            WHERE project_id = $projectId
            ORDER BY created_at_utc;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        var assets = new List<MediaAsset>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assets.Add(ReadMediaAsset(reader));
        }

        return assets;
    }

    public async Task SaveArtifactAsync(ProjectArtifact artifact, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO artifacts (
                id,
                project_id,
                media_asset_id,
                stage_run_id,
                kind,
                relative_path,
                sha256,
                size_bytes,
                duration_seconds,
                sample_rate,
                channel_count,
                provenance,
                created_at_utc,
                degradation_code,
                degradation_stage)
            VALUES (
                $id,
                $projectId,
                $mediaAssetId,
                $stageRunId,
                $kind,
                $relativePath,
                $sha256,
                $sizeBytes,
                $durationSeconds,
                $sampleRate,
                $channelCount,
                $provenance,
                $createdAtUtc,
                $degradationCode,
                $degradationStage)
            ON CONFLICT(id) DO UPDATE SET
                stage_run_id = excluded.stage_run_id,
                kind = excluded.kind,
                relative_path = excluded.relative_path,
                sha256 = excluded.sha256,
                size_bytes = excluded.size_bytes,
                duration_seconds = excluded.duration_seconds,
                sample_rate = excluded.sample_rate,
                channel_count = excluded.channel_count,
                provenance = excluded.provenance,
                created_at_utc = excluded.created_at_utc,
                degradation_code = excluded.degradation_code,
                degradation_stage = excluded.degradation_stage
            ON CONFLICT(project_id, relative_path) DO UPDATE SET
                id = excluded.id,
                media_asset_id = excluded.media_asset_id,
                stage_run_id = excluded.stage_run_id,
                kind = excluded.kind,
                sha256 = excluded.sha256,
                size_bytes = excluded.size_bytes,
                duration_seconds = excluded.duration_seconds,
                sample_rate = excluded.sample_rate,
                channel_count = excluded.channel_count,
                provenance = excluded.provenance,
                created_at_utc = excluded.created_at_utc,
                degradation_code = excluded.degradation_code,
                degradation_stage = excluded.degradation_stage;
            """;
        BindArtifact(command, artifact);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectArtifact>> GetArtifactsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!File.Exists(database.DatabasePath))
        {
            return [];
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   media_asset_id,
                   stage_run_id,
                   kind,
                   relative_path,
                   sha256,
                   size_bytes,
                   duration_seconds,
                   sample_rate,
                   channel_count,
                   provenance,
                   created_at_utc,
                   degradation_code,
                   degradation_stage
            FROM artifacts
            WHERE project_id = $projectId
            ORDER BY created_at_utc, relative_path;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        var results = new List<ProjectArtifact>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadArtifact(reader));
        }

        return results;
    }

    public async Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken)
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
            DELETE FROM artifacts
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", artifactId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        if (!File.Exists(database.DatabasePath))
            return null;
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, project_id, media_asset_id, stage_run_id, kind, relative_path,
                   sha256, size_bytes, duration_seconds, sample_rate, channel_count,
                   provenance, created_at_utc, degradation_code, degradation_stage
            FROM artifacts WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", artifactId.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadArtifact(reader) : null;
    }

    private static void BindMediaAsset(SqliteCommand command, MediaAsset asset)
    {
        command.Parameters.AddWithValue("$id", asset.Id.ToString("D"));
        command.Parameters.AddWithValue("$projectId", asset.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$sourceFilePath", asset.SourceFilePath);
        command.Parameters.AddWithValue("$sourceFileName", asset.SourceFileName);
        command.Parameters.AddWithValue("$fingerprintSha256", asset.FingerprintSha256);
        command.Parameters.AddWithValue("$sourceSizeBytes", asset.SourceSizeBytes);
        command.Parameters.AddWithValue("$sourceLastWriteTimeUtc", asset.SourceLastWriteTimeUtc.UtcDateTime);
        command.Parameters.AddWithValue("$formatName", asset.FormatName);
        command.Parameters.AddWithValue("$durationSeconds", asset.DurationSeconds);
        command.Parameters.AddWithValue("$hasAudio", asset.HasAudio ? 1 : 0);
        command.Parameters.AddWithValue("$hasVideo", asset.HasVideo ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", asset.CreatedAtUtc.UtcDateTime);
    }

    private static MediaAsset ReadMediaAsset(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? reader.GetString(3) : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
            reader.GetString(7),
            reader.GetDouble(8),
            reader.GetInt64(9) == 1,
            reader.GetInt64(10) == 1,
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc)));

    private static void BindArtifact(SqliteCommand command, ProjectArtifact artifact)
    {
        command.Parameters.AddWithValue("$id", artifact.Id.ToString("D"));
        command.Parameters.AddWithValue("$projectId", artifact.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$mediaAssetId", artifact.MediaAssetId.ToString("D"));
        command.Parameters.AddWithValue("$stageRunId", artifact.StageRunId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$kind", artifact.Kind.ToString());
        command.Parameters.AddWithValue("$relativePath", artifact.RelativePath);
        command.Parameters.AddWithValue("$sha256", artifact.Sha256);
        command.Parameters.AddWithValue("$sizeBytes", artifact.SizeBytes);
        command.Parameters.AddWithValue("$durationSeconds", (object?)artifact.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$sampleRate", (object?)artifact.SampleRate ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelCount", (object?)artifact.ChannelCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$provenance", (object?)artifact.Provenance ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", artifact.CreatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("$degradationCode", (object?)artifact.DegradationCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$degradationStage", (object?)artifact.DegradationStage ?? DBNull.Value);
    }

    private static ProjectArtifact ReadArtifact(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Enum.Parse<ArtifactKind>(reader.GetString(4), ignoreCase: true),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetDouble(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc)),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));

    private Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionLease.OpenAsync(database, scopedConnectionProvider, cancellationToken);
}
