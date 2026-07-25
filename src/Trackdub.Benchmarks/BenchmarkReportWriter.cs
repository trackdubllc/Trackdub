using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Domain;

namespace Trackdub.Benchmarks;

public static class BenchmarkReportWriter
{
    /// <summary>
    /// Options used for all report serialization (enums as strings). Reuse this for
    /// any ad-hoc serialization (e.g. batch aggregate reports) so JSON shape stays
    /// consistent with the per-run report files.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static async Task WriteAsync(
        BenchmarkReport report,
        ReportFormat format,
        CancellationToken cancellationToken)
    {
        if (format is ReportFormat.Console)
        {
            return;
        }

        var directory = Path.GetDirectoryName(report.ReportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            report.ReportPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, report, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteAsync(
        BenchmarkBatchReport report,
        ReportFormat format,
        CancellationToken cancellationToken)
    {
        if (format is ReportFormat.Console)
        {
            return;
        }

        var directory = Path.GetDirectoryName(report.ReportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            report.ReportPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, report, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteAsync(
        AudioPrepBenchmarkReport report,
        ReportFormat format,
        CancellationToken cancellationToken)
    {
        if (format is ReportFormat.Console)
        {
            return;
        }

        var directory = Path.GetDirectoryName(report.ReportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            report.ReportPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, report, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteAsync(
        DubbingBenchmarkReport report,
        ReportFormat format,
        CancellationToken cancellationToken)
    {
        if (format is ReportFormat.Console)
        {
            return;
        }

        var directory = Path.GetDirectoryName(report.ReportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            report.ReportPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, report, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }
}
