using System.Data.Common;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Dapper;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class StageRunRepository : IStageRunRepository
{
    public async Task CreateAsync(
        DbConnection connection,
        StageRunRecord stageRun,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO StageRuns (
                Id,
                ProjectId,
                StageName,
                Status,
                StartedAtUtc,
                CompletedAtUtc,
                FailureReason,
                RequestedProvider,
                SelectedProvider,
                RuntimeModelId,
                RuntimeModelAlias,
                RuntimeModelVariant,
                BootstrapDetail,
                DeviceTarget,
                FallbackReason,
                SmokeEvidenceId,
                BenchmarkEvidenceId)
            VALUES (
                @Id,
                @ProjectId,
                @StageName,
                @Status,
                @StartedAtUtc,
                @CompletedAtUtc,
                @FailureReason,
                @RequestedProvider,
                @SelectedProvider,
                @RuntimeModelId,
                @RuntimeModelAlias,
                @RuntimeModelVariant,
                @BootstrapDetail,
                @DeviceTarget,
                @FallbackReason,
                @SmokeEvidenceId,
                @BenchmarkEvidenceId);
            """,
            ToParameters(stageRun),
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<StageRunRecord?> GetAsync(
        DbConnection connection,
        Guid stageRunId,
        CancellationToken cancellationToken = default)
    {
        StageRunRow? row = await connection.QuerySingleOrDefaultAsync<StageRunRow>(new CommandDefinition(
            """
            SELECT
                Id,
                ProjectId,
                StageName,
                Status,
                StartedAtUtc,
                CompletedAtUtc,
                FailureReason,
                RequestedProvider,
                SelectedProvider,
                RuntimeModelId,
                RuntimeModelAlias,
                RuntimeModelVariant,
                BootstrapDetail,
                DeviceTarget,
                FallbackReason,
                SmokeEvidenceId,
                BenchmarkEvidenceId
            FROM StageRuns
            WHERE Id = @Id;
            """,
            new { Id = SqliteValueConverters.ToDbValue(stageRunId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<StageRunRecord>> ListByProjectAsync(
        DbConnection connection,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<StageRunRow> rows = await connection.QueryAsync<StageRunRow>(new CommandDefinition(
            """
            SELECT
                Id,
                ProjectId,
                StageName,
                Status,
                StartedAtUtc,
                CompletedAtUtc,
                FailureReason,
                RequestedProvider,
                SelectedProvider,
                RuntimeModelId,
                RuntimeModelAlias,
                RuntimeModelVariant,
                BootstrapDetail,
                DeviceTarget,
                FallbackReason,
                SmokeEvidenceId,
                BenchmarkEvidenceId
            FROM StageRuns
            WHERE ProjectId = @ProjectId
            ORDER BY StartedAtUtc, Id;
            """,
            new { ProjectId = SqliteValueConverters.ToDbValue(projectId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToDomain).ToArray();
    }

    public async Task CompleteAsync(
        DbConnection connection,
        StageRunRecord stageRun,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE StageRuns
            SET Status = @Status,
                CompletedAtUtc = @CompletedAtUtc,
                FailureReason = @FailureReason,
                RequestedProvider = @RequestedProvider,
                SelectedProvider = @SelectedProvider,
                RuntimeModelId = @RuntimeModelId,
                RuntimeModelAlias = @RuntimeModelAlias,
                RuntimeModelVariant = @RuntimeModelVariant,
                BootstrapDetail = @BootstrapDetail,
                DeviceTarget = @DeviceTarget,
                FallbackReason = @FallbackReason,
                SmokeEvidenceId = @SmokeEvidenceId,
                BenchmarkEvidenceId = @BenchmarkEvidenceId
            WHERE Id = @Id;
            """,
            ToParameters(stageRun),
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static object ToParameters(StageRunRecord stageRun) => new
    {
        Id = SqliteValueConverters.ToDbValue(stageRun.Id),
        ProjectId = SqliteValueConverters.ToDbValue(stageRun.ProjectId),
        stageRun.StageName,
        Status = stageRun.Status.ToString(),
        StartedAtUtc = SqliteValueConverters.ToDbValue(stageRun.StartedAtUtc),
        CompletedAtUtc = stageRun.CompletedAtUtc is DateTimeOffset completedAtUtc ? SqliteValueConverters.ToDbValue(completedAtUtc) : null,
        stageRun.FailureReason,
        RequestedProvider = stageRun.RuntimeInfo?.RequestedProvider,
        SelectedProvider = stageRun.RuntimeInfo?.SelectedProvider,
        RuntimeModelId = stageRun.RuntimeInfo?.ModelId,
        RuntimeModelAlias = stageRun.RuntimeInfo?.ModelAlias,
        RuntimeModelVariant = stageRun.RuntimeInfo?.ModelVariant,
        BootstrapDetail = stageRun.RuntimeInfo?.BootstrapDetail,
        DeviceTarget = stageRun.RuntimeInfo?.DeviceTarget,
        FallbackReason = stageRun.RuntimeInfo?.FallbackReason,
        SmokeEvidenceId = stageRun.RuntimeInfo?.SmokeEvidenceId,
        BenchmarkEvidenceId = stageRun.RuntimeInfo?.BenchmarkEvidenceId
    };

    private static StageRunRecord ToDomain(StageRunRow row) =>
        new(
            SqliteValueConverters.ParseGuid(row.Id),
            SqliteValueConverters.ParseGuid(row.ProjectId),
            row.StageName,
            Enum.Parse<StageRunStatus>(row.Status, ignoreCase: false),
            SqliteValueConverters.ParseDateTimeOffset(row.StartedAtUtc),
            string.IsNullOrWhiteSpace(row.CompletedAtUtc) ? null : SqliteValueConverters.ParseDateTimeOffset(row.CompletedAtUtc),
            row.FailureReason,
            string.IsNullOrWhiteSpace(row.RequestedProvider) || string.IsNullOrWhiteSpace(row.SelectedProvider)
                ? null
                : new StageRunRuntimeInfo(
                    row.RequestedProvider,
                    row.SelectedProvider,
                    row.RuntimeModelId,
                    row.RuntimeModelAlias,
                    row.RuntimeModelVariant,
                    row.BootstrapDetail,
                    row.DeviceTarget,
                    row.FallbackReason,
                    row.SmokeEvidenceId,
                    row.BenchmarkEvidenceId));

    private sealed record StageRunRow(
        string Id,
        string ProjectId,
        string StageName,
        string Status,
        string StartedAtUtc,
        string? CompletedAtUtc,
        string? FailureReason,
        string? RequestedProvider,
        string? SelectedProvider,
        string? RuntimeModelId,
        string? RuntimeModelAlias,
        string? RuntimeModelVariant,
        string? BootstrapDetail,
        string? DeviceTarget,
        string? FallbackReason,
        string? SmokeEvidenceId,
        string? BenchmarkEvidenceId);
}
