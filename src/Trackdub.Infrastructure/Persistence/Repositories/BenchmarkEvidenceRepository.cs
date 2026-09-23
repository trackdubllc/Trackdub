using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed partial class BenchmarkEvidenceRepository(SqliteUserBenchmarkDatabase database) : IBenchmarkEvidenceRepository
{
    private const int ObservationLimit = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private string ReportsDirectory => Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "benchmark-reports");

    public async Task SaveAsync(BenchmarkEvidenceReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion != 1 || report.RunId == Guid.Empty)
        {
            throw new ArgumentException("Unsupported evidence schema or empty run id.", nameof(report));
        }

        if (report.Kind == BenchmarkEvidenceKind.Benchmark &&
            report.Status == BenchmarkEvidenceStatus.Completed &&
            (report.FixtureSha256?.Length != 64 || !report.FixtureSha256.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException("Controlled benchmarks require a fixture SHA-256.", nameof(report));
        }

        BenchmarkEvidenceReport safe = Sanitize(report);
        string fileName = $"{report.RunId:N}.json";
        string destination = Path.Combine(ReportsDirectory, fileName);
        Directory.CreateDirectory(ReportsDirectory);
        string temporary = Path.Combine(ReportsDirectory, $".{report.RunId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(safe, JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
            await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO BenchmarkEvidenceReports (RunId, Kind, CompletedAtUtc, ReportFileName)
                VALUES (@RunId, @Kind, @CompletedAtUtc, @ReportFileName)
                ON CONFLICT(RunId) DO UPDATE SET
                    Kind = excluded.Kind,
                    CompletedAtUtc = excluded.CompletedAtUtc,
                    ReportFileName = excluded.ReportFileName;
                """,
                new
                {
                    RunId = report.RunId.ToString("D"),
                    Kind = report.Kind.ToString(),
                    CompletedAtUtc = report.CompletedAtUtc.ToUniversalTime().ToString("O"),
                    ReportFileName = fileName
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (report.Kind == BenchmarkEvidenceKind.Observation)
            {
                await PruneObservationsAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<BenchmarkEvidenceReport?> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) return null;
        string path = Path.Combine(ReportsDirectory, $"{runId:N}.json");
        if (!File.Exists(path)) return null;
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BenchmarkEvidenceReport>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BenchmarkEvidenceReport>> ListRecentAsync(
        BenchmarkEvidenceKind? kind, int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return [];
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<string> ids = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT RunId FROM BenchmarkEvidenceReports
            WHERE @Kind IS NULL OR Kind = @Kind
            ORDER BY CompletedAtUtc DESC, RunId DESC LIMIT @Limit;
            """,
            new { Kind = kind?.ToString(), Limit = limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var reports = new List<BenchmarkEvidenceReport>();
        foreach (string id in ids)
        {
            BenchmarkEvidenceReport? report = await GetAsync(Guid.Parse(id), cancellationToken).ConfigureAwait(false);
            if (report is not null) reports.Add(report);
        }
        return reports;
    }

    private async Task PruneObservationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        string cutoff = DateTimeOffset.UtcNow.AddDays(-90).ToString("O");
        string[] stale = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT RunId FROM BenchmarkEvidenceReports
            WHERE Kind = @Kind AND (CompletedAtUtc < @Cutoff OR RunId NOT IN (
                SELECT RunId FROM BenchmarkEvidenceReports WHERE Kind = @Kind
                ORDER BY CompletedAtUtc DESC, RunId DESC LIMIT @Limit));
            """,
            new { Kind = BenchmarkEvidenceKind.Observation.ToString(), Cutoff = cutoff, Limit = ObservationLimit },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        if (stale.Length == 0) return;
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM BenchmarkEvidenceReports WHERE RunId IN @Ids;",
            new { Ids = stale }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (string id in stale)
        {
            string path = Path.Combine(ReportsDirectory, $"{Guid.Parse(id):N}.json");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static BenchmarkEvidenceReport Sanitize(BenchmarkEvidenceReport report) => report with
    {
        StartedAtUtc = report.StartedAtUtc?.ToUniversalTime(),
        CompletedAtUtc = report.CompletedAtUtc.ToUniversalTime(),
        Scenario = Scrub(report.Scenario)!,
        RunMode = Scrub(report.RunMode)!,
        Reason = Scrub(report.Reason),
        RequestedModel = Scrub(report.RequestedModel),
        ActualModel = Scrub(report.ActualModel),
        RequestedProvider = Scrub(report.RequestedProvider),
        ActualProvider = Scrub(report.ActualProvider),
        Configuration = report.Configuration.ToDictionary(x => Scrub(x.Key)!, x => Scrub(x.Value)!),
        RuntimeVersions = report.RuntimeVersions.ToDictionary(x => Scrub(x.Key)!, x => Scrub(x.Value)!),
        TimingsMilliseconds = report.TimingsMilliseconds.ToDictionary(x => Scrub(x.Key)!, x => x.Value),
        MemoryBytes = report.MemoryBytes.ToDictionary(x => Scrub(x.Key)!, x => x.Value),
        Stages = report.Stages.Select(stage => stage with
        {
            StartedAtUtc = stage.StartedAtUtc?.ToUniversalTime(),
            CompletedAtUtc = stage.CompletedAtUtc?.ToUniversalTime(),
            Name = Scrub(stage.Name)!, Reason = Scrub(stage.Reason),
            RequestedModel = Scrub(stage.RequestedModel), ActualModel = Scrub(stage.ActualModel),
            RequestedProvider = Scrub(stage.RequestedProvider), ActualProvider = Scrub(stage.ActualProvider)
        }).ToArray()
    };

    private static string? Scrub(string? value) => value is null ? null :
        PathPattern().Replace(value, "[path]");

    [GeneratedRegex(@"(?:(?<!\w)[A-Za-z]:[\\/]|\\\\|(?<![\w:/])/)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}
