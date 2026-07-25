using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class UserBenchmarkRepository(SqliteUserBenchmarkDatabase database) : IUserBenchmarkRepository
{
    public async Task AddAsync(BenchmarkRunRecord run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        (Guid? evidenceId, string? fingerprintHash, string? scenario) = ParseProfilerMetadata(run.ModelId);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO BenchmarkRuns (
                Id,
                ModelId,
                ModelPath,
                ReportPath,
                Status,
                RequestedProvider,
                SelectedProvider,
                RunCount,
                SupportsExecution,
                ModelSizeBytes,
                ColdLoadMilliseconds,
                WarmLatencyAverageMilliseconds,
                WarmLatencyMinimumMilliseconds,
                WarmLatencyMaximumMilliseconds,
                FailureReason,
                GeneratedAtUtc,
                EvidenceId,
                FingerprintHash,
                Scenario)
            VALUES (
                @Id,
                @ModelId,
                @ModelPath,
                @ReportPath,
                @Status,
                @RequestedProvider,
                @SelectedProvider,
                @RunCount,
                @SupportsExecution,
                @ModelSizeBytes,
                @ColdLoadMilliseconds,
                @WarmLatencyAverageMilliseconds,
                @WarmLatencyMinimumMilliseconds,
                @WarmLatencyMaximumMilliseconds,
                @FailureReason,
                @GeneratedAtUtc,
                @EvidenceId,
                @FingerprintHash,
                @Scenario)
            ON CONFLICT(Id) DO UPDATE SET
                ModelId = excluded.ModelId,
                ModelPath = excluded.ModelPath,
                ReportPath = excluded.ReportPath,
                Status = excluded.Status,
                RequestedProvider = excluded.RequestedProvider,
                SelectedProvider = excluded.SelectedProvider,
                RunCount = excluded.RunCount,
                SupportsExecution = excluded.SupportsExecution,
                ModelSizeBytes = excluded.ModelSizeBytes,
                ColdLoadMilliseconds = excluded.ColdLoadMilliseconds,
                WarmLatencyAverageMilliseconds = excluded.WarmLatencyAverageMilliseconds,
                WarmLatencyMinimumMilliseconds = excluded.WarmLatencyMinimumMilliseconds,
                WarmLatencyMaximumMilliseconds = excluded.WarmLatencyMaximumMilliseconds,
                FailureReason = excluded.FailureReason,
                GeneratedAtUtc = excluded.GeneratedAtUtc,
                EvidenceId = excluded.EvidenceId,
                FingerprintHash = excluded.FingerprintHash,
                Scenario = excluded.Scenario;
            """,
            new
            {
                Id = SqliteValueConverters.ToDbValue(run.Id),
                run.ModelId,
                run.ModelPath,
                run.ReportPath,
                Status = run.Status.ToString(),
                run.RequestedProvider,
                run.SelectedProvider,
                run.RunCount,
                SupportsExecution = run.SupportsExecution ? 1 : 0,
                run.ModelSizeBytes,
                run.ColdLoadMilliseconds,
                run.WarmLatencyAverageMilliseconds,
                run.WarmLatencyMinimumMilliseconds,
                run.WarmLatencyMaximumMilliseconds,
                run.FailureReason,
                GeneratedAtUtc = SqliteValueConverters.ToDbValue(run.GeneratedAtUtc),
                EvidenceId = evidenceId is null ? null : SqliteValueConverters.ToDbValue(evidenceId.Value),
                FingerprintHash = fingerprintHash,
                Scenario = scenario
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ContainsEvidenceAsync(Guid evidenceId, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(1)
            FROM BenchmarkRuns
            WHERE EvidenceId = @EvidenceId;
            """,
            new { EvidenceId = SqliteValueConverters.ToDbValue(evidenceId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return count > 0;
    }

    public async Task<IReadOnlyList<BenchmarkRunRecord>> ListByEvidenceIdAsync(
        Guid evidenceId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<BenchmarkRunRow> rows = (await connection.QueryAsync<BenchmarkRunRow>(new CommandDefinition(
            """
            SELECT
                Id,
                ModelId,
                ModelPath,
                ReportPath,
                Status,
                RequestedProvider,
                SelectedProvider,
                RunCount,
                SupportsExecution,
                ModelSizeBytes,
                ColdLoadMilliseconds,
                WarmLatencyAverageMilliseconds,
                WarmLatencyMinimumMilliseconds,
                WarmLatencyMaximumMilliseconds,
                FailureReason,
                GeneratedAtUtc
            FROM BenchmarkRuns
            WHERE EvidenceId = @EvidenceId
            ORDER BY Scenario, GeneratedAtUtc;
            """,
            new { EvidenceId = SqliteValueConverters.ToDbValue(evidenceId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return rows.Select(MapRow).ToArray();
    }

    public async Task<IReadOnlyList<BenchmarkRunRecord>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<BenchmarkRunRow> rows = (await connection.QueryAsync<BenchmarkRunRow>(new CommandDefinition(
            """
            SELECT
                Id,
                ModelId,
                ModelPath,
                ReportPath,
                Status,
                RequestedProvider,
                SelectedProvider,
                RunCount,
                SupportsExecution,
                ModelSizeBytes,
                ColdLoadMilliseconds,
                WarmLatencyAverageMilliseconds,
                WarmLatencyMinimumMilliseconds,
                WarmLatencyMaximumMilliseconds,
                FailureReason,
                GeneratedAtUtc
            FROM BenchmarkRuns
            ORDER BY GeneratedAtUtc DESC, Id DESC
            LIMIT @Limit;
            """,
            new { Limit = limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return rows.Select(MapRow).ToArray();
    }

    internal static (Guid? EvidenceId, string? FingerprintHash, string? Scenario) ParseProfilerMetadata(string modelId)
    {
        const string prefix = "hardware-profiler:";
        if (!modelId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return (null, null, null);
        }

        string[] parts = modelId[prefix.Length..].Split(':', 3);
        if (parts.Length < 3)
        {
            return (null, null, null);
        }

        return Guid.TryParse(parts[0], out Guid evidenceId)
            ? (evidenceId, parts[1], parts[2])
            : (null, null, null);
    }

    internal static string BuildProfilerModelId(Guid evidenceId, string fingerprintHash, string scenarioName) =>
        $"hardware-profiler:{evidenceId:D}:{fingerprintHash}:{scenarioName}";

    private static BenchmarkRunRecord MapRow(BenchmarkRunRow row) =>
        new(
            SqliteValueConverters.ParseGuid(row.Id),
            row.ModelId,
            row.ModelPath,
            row.ReportPath,
            Enum.TryParse(row.Status, ignoreCase: false, out BenchmarkStatus status)
                ? status
                : BenchmarkStatus.Failed,
            row.RequestedProvider,
            row.SelectedProvider,
            (int)row.RunCount,
            row.SupportsExecution != 0,
            row.ModelSizeBytes,
            row.ColdLoadMilliseconds,
            row.WarmLatencyAverageMilliseconds,
            row.WarmLatencyMinimumMilliseconds,
            row.WarmLatencyMaximumMilliseconds,
            row.FailureReason,
            SqliteValueConverters.ParseDateTimeOffset(row.GeneratedAtUtc));

    private sealed record BenchmarkRunRow(
        string Id,
        string ModelId,
        string ModelPath,
        string ReportPath,
        string Status,
        string RequestedProvider,
        string SelectedProvider,
        long RunCount,
        long SupportsExecution,
        long ModelSizeBytes,
        double? ColdLoadMilliseconds,
        double? WarmLatencyAverageMilliseconds,
        double? WarmLatencyMinimumMilliseconds,
        double? WarmLatencyMaximumMilliseconds,
        string? FailureReason,
        string GeneratedAtUtc);
}
