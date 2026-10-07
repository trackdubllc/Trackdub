using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Trackdub.Contracts;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

/// <summary>
/// BEGIN IMMEDIATE reserves the single SQLite writer before checking heads. All checks,
/// conditional inserts, stale marking and publication share this connection/transaction.
/// Independent readers can continue to observe the previous committed snapshot in WAL mode.
/// </summary>
public sealed class SqliteAtomicRevisionPersistence(SqliteProjectDatabase database, IArtifactStore artifactStore)
    : IAtomicRevisionPersistence
{
    // Test seams forward to the real transaction; production always commits the SQLite transaction.
    internal Func<SqliteTransaction, CancellationToken, Task> CommitTransactionAsync { get; set; } =
        static (transaction, ct) => transaction.CommitAsync(ct);
    internal Func<CancellationToken, Task>? BeforePublicationAsync { get; set; }
    internal Func<CancellationToken, Task<SqliteConnection>> OpenRecoveryConnectionAsync { get; set; } = database.OpenConnectionAsync;

    public async Task<TranslationRevision> CommitRevisionAsync(
        AtomicRevisionCommitRequest request,
        Func<TranslationRevision, CancellationToken, Task<PreparedCommitArtifact>> prepareArtifact,
        CancellationToken cancellationToken)
    {
        TranslationRevision revision = request.Revision;
        PreparedCommitArtifact? prepared = null;
        try
        {
            await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            // Prepare/fingerprint bytes before reserving the writer. This number is provisional;
            // the authoritative allocation is checked again under the write lock below.
            revision = revision with { RevisionNumber = await ReadNextNumberAsync(connection, null, revision, cancellationToken).ConfigureAwait(false) };
            prepared = await prepareArtifact(revision, cancellationToken).ConfigureAwait(false);
            ValidateArtifact(prepared, revision.ProjectId, ArtifactKind.TranslationRevision);
            using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            try
            {
                Guid? source = await ReadHeadAsync(connection, transaction, revision.ProjectId, null, cancellationToken).ConfigureAwait(false);
                Guid? previous = await ReadHeadAsync(connection, transaction, revision.ProjectId, revision.TargetLanguage, cancellationToken).ConfigureAwait(false);
                if (source != revision.SourceTranscriptRevisionId || previous != request.ExpectedPreviousTranslationRevisionId)
                {
                    throw new InvalidDataException("Revision commit conflicted with the expected source or translation revision.");
                }

                var previousSegments = previous is { } id
                    ? await ReadSegmentsAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)
                    : new Dictionary<int, TranslatedSegment>();
                int nextNumber = await ReadNextNumberAsync(connection, transaction, revision, cancellationToken).ConfigureAwait(false);
                if (revision.RevisionNumber != nextNumber)
                    throw new InvalidDataException("Revision number allocation conflicted with staged artifact content.");
                await SqliteTranslationRepository.SaveRevisionAsync(connection, transaction, revision, request.Segments, cancellationToken,
                    guardExpectedHeads: true, expectedPrevious: previous).ConfigureAwait(false);
                await SqliteMediaAssetRepository.SaveArtifactAsync(connection, transaction, prepared.Artifact, cancellationToken, createOnly: true).ConfigureAwait(false);
                await MarkChangedTakesAsync(connection, transaction, revision, previousSegments, request.Segments, cancellationToken).ConfigureAwait(false);
                await PublishAsync(transaction, prepared, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception original) when (prepared is not null)
        {
            // Original connection and transaction have disposed before fresh-state verification.
            if (!await ResolveFailureAsync(revision.ProjectId, revision.Id, isTake: false, prepared, original).ConfigureAwait(false))
            {
                throw;
            }
        }
        finally
        {
            if (prepared is not null) await prepared.DisposeAsync().ConfigureAwait(false);
        }

        return revision;
    }

    public async Task ValidateTakeInputAsync(AtomicTakeInput input, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await GuardTakeAsync(connection, transaction, input, cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task CommitTakeAsync(AtomicTakeCommitRequest request, PreparedCommitArtifact artifact, CancellationToken cancellationToken)
    {
        await using (artifact.ConfigureAwait(false))
        {
            try
            {
                await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                try
                {
                    await GuardTakeAsync(connection, transaction, request.Input, cancellationToken).ConfigureAwait(false);
                    ValidateArtifact(artifact, request.Input.ProjectId, ArtifactKind.TtsTake);
                    await SqliteMediaAssetRepository.SaveArtifactAsync(connection, transaction, artifact.Artifact, cancellationToken, createOnly: true).ConfigureAwait(false);
                    await SqliteTtsTakeRepository.SaveAsync(connection, transaction, request.Take, cancellationToken, createOnly: true).ConfigureAwait(false);
                    await PublishAsync(transaction, artifact, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await RollbackAsync(transaction).ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception original)
            {
                if (!await ResolveFailureAsync(request.Input.ProjectId, request.Take.Id, isTake: true, artifact, original).ConfigureAwait(false)) throw;
            }
        }
    }

    private async Task PublishAsync(SqliteTransaction transaction, PreparedCommitArtifact artifact, CancellationToken ct)
    {
        if (BeforePublicationAsync is { } before) await before(ct).ConfigureAwait(false);
        await artifactStore.CommitNewAsync(artifact.Handle, artifact.Receipt, ct).ConfigureAwait(false);
        await CommitTransactionAsync(transaction, ct).ConfigureAwait(false);
        // Successful commit is the point of publication. Late cancellation cannot undo it.
    }

    private static async Task RollbackAsync(SqliteTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException) { /* Verify durable state after disposal. */ }
    }

    private async Task<bool> ResolveFailureAsync(Guid projectId, Guid outputId, bool isTake, PreparedCommitArtifact prepared, Exception original)
    {
        try
        {
            await using SqliteConnection connection = await OpenRecoveryConnectionAsync(CancellationToken.None).ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = isTake
                ? "SELECT COUNT(*) FROM tts_takes WHERE id = $id AND project_id = $project AND artifact_id = $artifact;"
                : "SELECT COUNT(*) FROM translation_revisions WHERE id = $id AND project_id = $project;";
            command.Parameters.AddWithValue("$id", outputId.ToString("D"));
            command.Parameters.AddWithValue("$project", projectId.ToString("D"));
            if (isTake) command.Parameters.AddWithValue("$artifact", prepared.Artifact.Id.ToString("D"));
            bool outputExists = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false)) == 1;
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM artifacts WHERE id = $id AND project_id = $project AND relative_path = $path AND sha256 = $sha;";
            command.Parameters.AddWithValue("$id", prepared.Artifact.Id.ToString("D"));
            command.Parameters.AddWithValue("$project", projectId.ToString("D"));
            command.Parameters.AddWithValue("$path", prepared.Artifact.RelativePath);
            command.Parameters.AddWithValue("$sha", prepared.Artifact.Sha256);
            bool artifactExists = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false)) == 1;
            if (outputExists && artifactExists) return true;
            if (outputExists || artifactExists) throw new IOException("Output publication has inconsistent durable metadata.");

            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM artifacts WHERE REPLACE(relative_path, CHAR(92), '/') = $path COLLATE NOCASE;";
            command.Parameters.AddWithValue("$path", prepared.Artifact.RelativePath.Replace('\\', '/'));
            bool referenced = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false)) != 0;
            if (!referenced && prepared.Receipt.CreatedByAttempt && File.Exists(prepared.Receipt.FinalPath))
            {
                string checksum;
                await using (FileStream file = File.OpenRead(prepared.Receipt.FinalPath))
                    checksum = Convert.ToHexString(await SHA256.HashDataAsync(file, CancellationToken.None).ConfigureAwait(false));
                if (string.Equals(checksum, prepared.Receipt.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    File.Delete(prepared.Receipt.FinalPath);
            }

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        catch (Exception verificationFailure)
        {
            throw new IOException("Artifact publication could not be resolved; attempt files were preserved. " + verificationFailure.Message, original);
        }
    }

    private static void ValidateArtifact(PreparedCommitArtifact artifact, Guid projectId, ArtifactKind kind)
    {
        if (artifact.Artifact.ProjectId != projectId || artifact.Artifact.Kind != kind
            || artifact.Handle.RelativePath.Replace('\\', '/') != artifact.Artifact.RelativePath.Replace('\\', '/')
            || !string.Equals(artifact.Artifact.Sha256, artifact.Receipt.ExpectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Prepared artifact does not match its commit.");
    }

    private static async Task GuardTakeAsync(SqliteConnection connection, SqliteTransaction transaction, AtomicTakeInput input, CancellationToken ct)
    {
        if (await ReadHeadAsync(connection, transaction, input.ProjectId, null, ct).ConfigureAwait(false) != input.ExpectedSourceRevisionId
            || await ReadHeadAsync(connection, transaction, input.ProjectId, input.TargetLanguage.Trim().ToLowerInvariant(), ct).ConfigureAwait(false) != input.ExpectedTranslationRevisionId
            || input.Segment.TranslationRevisionId != input.ExpectedTranslationRevisionId)
            throw new InvalidDataException("Take commit conflicted with its source or translation revision.");

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM translated_segments s JOIN translation_revisions r ON r.id = s.translation_revision_id
            WHERE s.id = $id AND s.translation_revision_id = $revision AND r.project_id = $project
              AND r.source_transcript_revision_id = $source AND s.segment_index = $index
              AND s.text = $text AND s.start_seconds = $start AND s.end_seconds = $end AND s.source_segment_hash IS $hash;
            """;
        command.Parameters.AddWithValue("$id", input.Segment.Id.ToString("D"));
        command.Parameters.AddWithValue("$revision", input.ExpectedTranslationRevisionId.ToString("D"));
        command.Parameters.AddWithValue("$project", input.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$source", input.ExpectedSourceRevisionId.ToString("D"));
        command.Parameters.AddWithValue("$index", input.Segment.SegmentIndex);
        command.Parameters.AddWithValue("$text", input.Segment.Text);
        command.Parameters.AddWithValue("$start", input.Segment.StartSeconds);
        command.Parameters.AddWithValue("$end", input.Segment.EndSeconds);
        command.Parameters.AddWithValue("$hash", input.Segment.SourceSegmentHash ?? (object)DBNull.Value);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1)
            throw new InvalidDataException("Take synthesis input is not the committed translated segment.");
    }

    private static async Task<Guid?> ReadHeadAsync(SqliteConnection connection, SqliteTransaction transaction, Guid projectId, string? language, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = language is null
            ? "SELECT id FROM transcript_revisions WHERE project_id = $project ORDER BY revision_number DESC LIMIT 1;"
            : "SELECT id FROM translation_revisions WHERE project_id = $project AND target_language = $language ORDER BY revision_number DESC LIMIT 1;";
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        if (language is not null) command.Parameters.AddWithValue("$language", language);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string id ? Guid.Parse(id) : null;
    }

    private static async Task<int> ReadNextNumberAsync(SqliteConnection connection, SqliteTransaction? transaction, TranslationRevision revision, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(revision_number), 0) + 1 FROM translation_revisions WHERE project_id = $project AND target_language = $language;";
        command.Parameters.AddWithValue("$project", revision.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$language", revision.TargetLanguage);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static async Task<Dictionary<int, TranslatedSegment>> ReadSegmentsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, segment_index, start_seconds, end_seconds, text, source_segment_hash FROM translated_segments WHERE translation_revision_id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new Dictionary<int, TranslatedSegment>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var segment = new TranslatedSegment(Guid.Parse(reader.GetString(0)), id, reader.GetInt32(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            results.Add(segment.SegmentIndex, segment);
        }
        return results;
    }

    private static async Task MarkChangedTakesAsync(SqliteConnection connection, SqliteTransaction transaction, TranslationRevision revision,
        Dictionary<int, TranslatedSegment> previous, IReadOnlyList<TranslatedSegment> segments, CancellationToken ct)
    {
        var next = segments.ToDictionary(s => s.SegmentIndex);
        int[] changed = previous.Keys.Union(next.Keys).Where(index => !previous.TryGetValue(index, out var old)
            || !next.TryGetValue(index, out var current) || old.Text != current.Text || old.StartSeconds != current.StartSeconds
            || old.EndSeconds != current.EndSeconds || old.SourceSegmentHash != current.SourceSegmentHash).ToArray();
        foreach (int index in changed)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE tts_takes SET is_stale = 1, status = $status
                WHERE project_id = $project AND translated_segment_id IN (
                    SELECT s.id FROM translated_segments s JOIN translation_revisions r ON r.id = s.translation_revision_id
                    WHERE r.project_id = $project AND r.target_language = $language AND r.id != $newRevision AND s.segment_index = $index);
                """;
            command.Parameters.AddWithValue("$project", revision.ProjectId.ToString("D"));
            command.Parameters.AddWithValue("$language", revision.TargetLanguage);
            command.Parameters.AddWithValue("$newRevision", revision.Id.ToString("D"));
            command.Parameters.AddWithValue("$index", index);
            command.Parameters.AddWithValue("$status", TtsTakeStatus.Stale.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
