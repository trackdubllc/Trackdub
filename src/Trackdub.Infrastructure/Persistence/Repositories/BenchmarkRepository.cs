using System.Data.Common;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Dapper;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class BenchmarkRepository : IBenchmarkRepository
{
    public async Task AddAsync(
        DbConnection connection,
        BenchmarkRunRecord run,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
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
                GeneratedAtUtc)
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
                @GeneratedAtUtc);
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
                GeneratedAtUtc = SqliteValueConverters.ToDbValue(run.GeneratedAtUtc)
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BenchmarkRunRecord>> ListByModelIdAsync(
        DbConnection connection,
        string modelId,
        CancellationToken cancellationToken = default)
    {
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
            WHERE ModelId = @ModelId
            ORDER BY GeneratedAtUtc, Id;
            """,
            new { ModelId = modelId },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return rows
            .Select(row => new BenchmarkRunRecord(
                SqliteValueConverters.ParseGuid(row.Id),
                row.ModelId,
                row.ModelPath,
                row.ReportPath,
                Enum.Parse<BenchmarkStatus>(row.Status, ignoreCase: false),
                row.RequestedProvider,
                row.SelectedProvider,
                row.RunCount,
                row.SupportsExecution,
                row.ModelSizeBytes,
                row.ColdLoadMilliseconds,
                row.WarmLatencyAverageMilliseconds,
                row.WarmLatencyMinimumMilliseconds,
                row.WarmLatencyMaximumMilliseconds,
                row.FailureReason,
                SqliteValueConverters.ParseDateTimeOffset(row.GeneratedAtUtc)))
            .ToArray();
    }

    private sealed record BenchmarkRunRow(
        string Id,
        string ModelId,
        string ModelPath,
        string ReportPath,
        string Status,
        string RequestedProvider,
        string SelectedProvider,
        int RunCount,
        bool SupportsExecution,
        long ModelSizeBytes,
        double? ColdLoadMilliseconds,
        double? WarmLatencyAverageMilliseconds,
        double? WarmLatencyMinimumMilliseconds,
        double? WarmLatencyMaximumMilliseconds,
        string? FailureReason,
        string GeneratedAtUtc);
}
