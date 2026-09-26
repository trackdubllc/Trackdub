using System.Globalization;
using System.Text;
using System.Text.Json;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Reports;

/// <summary>
/// Exports benchmark evidence and execution provider matrix reports to JSON and Markdown formats.
/// Provides deterministic, machine-readable output suitable for CI quality gates and archival.
/// </summary>
public static class BenchmarkReportExporter
{
    /// <summary>
    /// Exports a <see cref="BenchmarkEvidenceReport"/> as machine-readable JSON.
    /// </summary>
    public static async Task ExportJsonAsync(
        BenchmarkEvidenceReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        EnsureDirectoryExists(outputPath);
        await using var stream = CreateFileStream(outputPath);
        await JsonSerializer.SerializeAsync(stream, report, BenchmarkReportWriter.SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Exports an <see cref="ExecutionProviderMatrixReport"/> as machine-readable JSON.
    /// </summary>
    public static async Task ExportJsonAsync(
        ExecutionProviderMatrixReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        EnsureDirectoryExists(outputPath);
        await using var stream = CreateFileStream(outputPath);
        await JsonSerializer.SerializeAsync(stream, report, BenchmarkReportWriter.SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Exports a <see cref="BenchmarkEvidenceReport"/> as human-readable Markdown.
    /// </summary>
    public static async Task ExportMarkdownAsync(
        BenchmarkEvidenceReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        EnsureDirectoryExists(outputPath);
        string markdown = RenderEvidenceMarkdown(report);
        await File.WriteAllTextAsync(outputPath, markdown, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Exports an <see cref="ExecutionProviderMatrixReport"/> as human-readable Markdown.
    /// </summary>
    public static async Task ExportMarkdownAsync(
        ExecutionProviderMatrixReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        EnsureDirectoryExists(outputPath);
        string markdown = RenderMatrixMarkdown(report);
        await File.WriteAllTextAsync(outputPath, markdown, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Exports both JSON and Markdown representations of a <see cref="BenchmarkEvidenceReport"/>
    /// into the specified directory.
    /// </summary>
    public static async Task ExportAllAsync(
        BenchmarkEvidenceReport report,
        string outputDirectory,
        string fileNameBase = "benchmark-evidence",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        string jsonPath = Path.Join(outputDirectory, $"{fileNameBase}.json");
        string markdownPath = Path.Join(outputDirectory, $"{fileNameBase}.md");

        await ExportJsonAsync(report, jsonPath, cancellationToken).ConfigureAwait(false);
        await ExportMarkdownAsync(report, markdownPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exports both JSON and Markdown representations of an <see cref="ExecutionProviderMatrixReport"/>
    /// into the specified directory.
    /// </summary>
    public static async Task ExportAllAsync(
        ExecutionProviderMatrixReport report,
        string outputDirectory,
        string fileNameBase = "execution-provider-matrix",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        string jsonPath = Path.Join(outputDirectory, $"{fileNameBase}.json");
        string markdownPath = Path.Join(outputDirectory, $"{fileNameBase}.md");

        await ExportJsonAsync(report, jsonPath, cancellationToken).ConfigureAwait(false);
        await ExportMarkdownAsync(report, markdownPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deserializes a <see cref="BenchmarkEvidenceReport"/> from a JSON file for validation.
    /// </summary>
    public static async Task<BenchmarkEvidenceReport?> LoadEvidenceJsonAsync(
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);

        await using var stream = new FileStream(
            jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<BenchmarkEvidenceReport>(
            stream, BenchmarkReportWriter.SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deserializes an <see cref="ExecutionProviderMatrixReport"/> from a JSON file for validation.
    /// </summary>
    public static async Task<ExecutionProviderMatrixReport?> LoadMatrixJsonAsync(
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);

        await using var stream = new FileStream(
            jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<ExecutionProviderMatrixReport>(
            stream, BenchmarkReportWriter.SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that a JSON file contains a structurally valid <see cref="ExecutionProviderMatrixReport"/>.
    /// Returns null if valid, or an error message describing what failed.
    /// </summary>
    public static async Task<string?> ValidateMatrixJsonAsync(
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ExecutionProviderMatrixReport? report = await LoadMatrixJsonAsync(jsonPath, cancellationToken)
                .ConfigureAwait(false);

            if (report is null)
                return "Deserialized report is null.";
            if (string.IsNullOrWhiteSpace(report.Scenario))
                return "Report scenario is missing or empty.";
            if (string.IsNullOrWhiteSpace(report.BaselineProvider))
                return "Report baseline provider is missing or empty.";
            if (report.Comparisons.Count == 0)
                return "Report contains no provider comparisons.";

            foreach (ProviderComparisonMetrics comparison in report.Comparisons)
            {
                if (string.IsNullOrWhiteSpace(comparison.Provider))
                    return "A comparison entry has a missing or empty provider name.";
                if (!double.IsFinite(comparison.P50Milliseconds))
                    return $"Provider '{comparison.Provider}' has non-finite P50 latency.";
                if (!double.IsFinite(comparison.SpeedupFactor))
                    return $"Provider '{comparison.Provider}' has non-finite speedup factor.";
                if (!double.IsFinite(comparison.ThroughputRatio))
                    return $"Provider '{comparison.Provider}' has non-finite throughput ratio.";
            }

            return null;
        }
        catch (JsonException ex)
        {
            return $"JSON deserialization failed: {ex.Message}";
        }
        catch (IOException ex)
        {
            return $"File I/O failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Validates that a JSON file contains a structurally valid <see cref="BenchmarkEvidenceReport"/>.
    /// Returns null if valid, or an error message describing what failed.
    /// </summary>
    public static async Task<string?> ValidateEvidenceJsonAsync(
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            BenchmarkEvidenceReport? report = await LoadEvidenceJsonAsync(jsonPath, cancellationToken)
                .ConfigureAwait(false);

            if (report is null)
                return "Deserialized report is null.";
            if (report.RunId == Guid.Empty)
                return "Report RunId is empty.";
            if (string.IsNullOrWhiteSpace(report.Scenario))
                return "Report scenario is missing or empty.";
            if (string.IsNullOrWhiteSpace(report.RunMode))
                return "Report run mode is missing or empty.";

            return null;
        }
        catch (JsonException ex)
        {
            return $"JSON deserialization failed: {ex.Message}";
        }
        catch (IOException ex)
        {
            return $"File I/O failed: {ex.Message}";
        }
    }

    public static string RenderEvidenceMarkdown(BenchmarkEvidenceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Benchmark Evidence Report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Field | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Run ID | `{report.RunId:N}` |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Scenario | {report.Scenario} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Run Mode | {report.RunMode} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Status | {report.Status} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Completed | {report.CompletedAtUtc:O} |");

        if (report.RequestedProvider is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Requested Provider | {report.RequestedProvider} |");
        if (report.ActualProvider is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Actual Provider | {report.ActualProvider} |");

        // Timings
        if (report.TimingsMilliseconds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Timing Metrics (ms)");
            sb.AppendLine();
            sb.AppendLine("| Metric | Value (ms) |");
            sb.AppendLine("| --- | ---: |");
            foreach ((string key, double? value) in report.TimingsMilliseconds)
            {
                string formatted = value.HasValue
                    ? value.Value.ToString("F2", CultureInfo.InvariantCulture)
                    : "—";
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {key} | {formatted} |");
            }
        }

        // Memory
        if (report.MemoryBytes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Memory Metrics");
            sb.AppendLine();
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("| --- | ---: |");
            foreach ((string key, long? value) in report.MemoryBytes)
            {
                string formatted = value.HasValue
                    ? FormatBytes(value.Value)
                    : "—";
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {key} | {formatted} |");
            }
        }

        if (report.ResourceTelemetry.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Resource Validation");
            sb.AppendLine();
            sb.AppendLine("Process-wide CPU, excluding child processes; memory maxima are endpoint-sampled.");
            sb.AppendLine("Threshold is an inclusive maximum for usage metrics and an inclusive minimum for the free-VRAM floor.");
            sb.AppendLine();
            sb.AppendLine("| Stage | Phase | Iteration | Attempt | Metric | Observed | Threshold | Status | Reason |");
            sb.AppendLine("| --- | --- | ---: | ---: | --- | ---: | ---: | --- | --- |");
            foreach (BenchmarkStageResourceTelemetry sample in report.ResourceTelemetry)
            {
                foreach (var check in sample.Validation.Checks)
                {
                    string observed = check.ObservedValue?.ToString("G", CultureInfo.InvariantCulture) ?? "unavailable";
                    string threshold = check.Threshold?.ToString("G", CultureInfo.InvariantCulture) ?? "not configured";
                    string reason = EscapeMarkdownCell(check.Reason ?? sample.Reason);
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"| {EscapeMarkdownCell(sample.Stage)} | {EscapeMarkdownCell(sample.Phase)} | {sample.Iteration} | {sample.Attempt} | {EscapeMarkdownCell(check.Metric)} | {observed} | {threshold} | {check.Status} | {reason} |");
                }
            }
        }

        if (report.ResourceDistribution.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Resource Distribution");
            sb.AppendLine();
            sb.AppendLine("Percentiles over all iterations of each stage/phase (Type 7, same formula as latency percentiles).");
            sb.AppendLine();
            sb.AppendLine("| Stage | Phase | Metric | N | Unavailable | Failed | Min | P50 | P95 | P99 | Max | Threshold | Status |");
            sb.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var distribution in report.ResourceDistribution)
            {
                foreach (var metric in distribution.Metrics)
                {
                    string threshold = metric.Threshold?.ToString("G", CultureInfo.InvariantCulture) ?? "not configured";
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"| {EscapeMarkdownCell(distribution.Stage)} | {EscapeMarkdownCell(distribution.Phase)} | {EscapeMarkdownCell(metric.Metric)} | {metric.SampleCount} | {metric.UnavailableSampleCount} | {metric.FailingSampleCount} | {metric.Minimum} | {metric.P50} | {metric.P95} | {metric.P99} | {metric.Maximum} | {threshold} | {metric.Status} |");
                }
            }
        }

        // Stages
        if (report.Stages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Stage Results");
            sb.AppendLine();
            sb.AppendLine("| Stage | Status | Duration (ms) | Provider |");
            sb.AppendLine("| --- | --- | ---: | --- |");
            foreach (BenchmarkEvidenceStage stage in report.Stages)
            {
                string duration = stage.DurationMilliseconds.HasValue
                    ? stage.DurationMilliseconds.Value.ToString("F1", CultureInfo.InvariantCulture)
                    : "—";
                string provider = stage.ActualProvider ?? "—";
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {stage.Name} | {stage.Status} | {duration} | {provider} |");
            }
        }

        return sb.ToString();
    }

    public static string RenderMatrixMarkdown(ExecutionProviderMatrixReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Execution Provider Matrix Report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Scenario:** {report.Scenario}  ");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Baseline Provider:** {report.BaselineProvider}  ");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Timestamp:** {report.Timestamp:O}  ");
        sb.AppendLine();

        sb.AppendLine("## Provider Comparison");
        sb.AppendLine();
        sb.AppendLine("| Provider | P50 (ms) | Speedup | Latency Δ (ms) | Throughput Ratio | Peak WS Δ | Managed Alloc Δ |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (ProviderComparisonMetrics comp in report.Comparisons)
        {
            string p50 = comp.P50Milliseconds.ToString("F1", CultureInfo.InvariantCulture);
            string speedup = comp.SpeedupFactor.ToString("F2", CultureInfo.InvariantCulture) + "x";
            string latencyDelta = FormatDelta(comp.LatencyDeltaMilliseconds, "F1");
            string throughput = comp.ThroughputRatio.ToString("F2", CultureInfo.InvariantCulture) + "x";
            string peakWsDelta = FormatBytesDelta(comp.PeakWorkingSetDeltaBytes);
            string managedDelta = FormatBytesDelta(comp.ManagedAllocatedDeltaBytes);

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {comp.Provider} | {p50} | {speedup} | {latencyDelta} | {throughput} | {peakWsDelta} | {managedDelta} |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes text for a pipe-delimited markdown table cell: a literal <c>|</c> would otherwise
    /// split the row across columns, and an embedded newline would split it across rows.
    /// </summary>
    private static string EscapeMarkdownCell(string? value) =>
        value is null ? string.Empty : value.Replace("|", "\\|").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    private static string FormatBytes(long bytes)
    {
        if (Math.Abs(bytes) >= 1024L * 1024 * 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):F2} GB");
        if (Math.Abs(bytes) >= 1024L * 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):F2} MB");
        if (Math.Abs(bytes) >= 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F2} KB");
        return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
    }

    private static string FormatDelta(double value, string format)
    {
        string formatted = Math.Abs(value).ToString(format, CultureInfo.InvariantCulture);
        return value switch
        {
            > 0 => $"+{formatted}",
            < 0 => $"-{formatted}",
            _ => formatted,
        };
    }

    private static string FormatBytesDelta(long bytes)
    {
        string formatted = FormatBytes(Math.Abs(bytes));
        return bytes switch
        {
            > 0 => $"+{formatted}",
            < 0 => $"-{formatted}",
            _ => formatted,
        };
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static FileStream CreateFileStream(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
}
