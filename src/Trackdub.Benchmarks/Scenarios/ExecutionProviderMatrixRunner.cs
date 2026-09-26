using Microsoft.Extensions.DependencyInjection;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Scenarios;

/// <summary>
/// Comparative execution provider benchmark runner and analysis calculator.
/// Evaluates inference workloads across configured execution providers (such as CPU, DirectML, and TensorRT)
/// and calculates speedup factors, latency deltas, throughput ratios, and resource trade-offs against a baseline.
/// </summary>
public sealed class ExecutionProviderMatrixRunner : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    /// <summary>
    /// Pure mathematical comparative calculation across execution provider performance statistics.
    /// </summary>
    /// <param name="scenario">The benchmark scenario name.</param>
    /// <param name="baselineProvider">The reference baseline provider (typically "cpu").</param>
    /// <param name="providerStats">Per-provider statistics dictionary mapping provider name to (P50, Throughput, PeakMemory, ManagedAlloc).</param>
    /// <param name="timestamp">Optional timestamp for report metadata.</param>
    /// <returns>A complete <see cref="ExecutionProviderMatrixReport"/> with comparative metrics.</returns>
    /// <exception cref="ArgumentException">Thrown when scenario or baselineProvider is empty, or baselineProvider is not in providerStats.</exception>
    public static ExecutionProviderMatrixReport CompareProviders(
        string scenario,
        string baselineProvider,
        IReadOnlyDictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)> providerStats,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineProvider);
        ArgumentNullException.ThrowIfNull(providerStats);

        // Find baseline with case-insensitive and normalized fallback
        string? matchedBaselineKey = null;
        (double P50, double Throughput, long PeakMemory, long ManagedAlloc) baseline = default;

        foreach (var kvp in providerStats)
        {
            if (string.Equals(kvp.Key, baselineProvider, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BenchmarkComparison.NormalizeProvider(kvp.Key), BenchmarkComparison.NormalizeProvider(baselineProvider), StringComparison.OrdinalIgnoreCase))
            {
                matchedBaselineKey = kvp.Key;
                baseline = kvp.Value;
                break;
            }
        }

        if (matchedBaselineKey is null)
        {
            throw new ArgumentException($"Baseline provider '{baselineProvider}' was not found in provider stats.", nameof(baselineProvider));
        }

        var comparisons = new List<ProviderComparisonMetrics>(providerStats.Count);
        foreach ((string provider, var stats) in providerStats)
        {
            double speedup;
            if (stats.P50 > 0 && baseline.P50 > 0)
            {
                speedup = baseline.P50 / stats.P50;
                if (double.IsNaN(speedup) || double.IsInfinity(speedup))
                {
                    speedup = 1.0;
                }
            }
            else
            {
                speedup = 1.0;
            }

            double latencyDelta = stats.P50 - baseline.P50;

            double throughputRatio;
            if (baseline.Throughput > 0)
            {
                throughputRatio = stats.Throughput / baseline.Throughput;
                if (double.IsNaN(throughputRatio) || double.IsInfinity(throughputRatio))
                {
                    throughputRatio = 1.0;
                }
            }
            else
            {
                throughputRatio = 1.0;
            }

            long peakMemoryDelta = stats.PeakMemory - baseline.PeakMemory;
            long managedAllocDelta = stats.ManagedAlloc - baseline.ManagedAlloc;

            comparisons.Add(new ProviderComparisonMetrics(
                Provider: provider,
                P50Milliseconds: stats.P50,
                SpeedupFactor: speedup,
                LatencyDeltaMilliseconds: latencyDelta,
                ThroughputRatio: throughputRatio,
                PeakWorkingSetDeltaBytes: peakMemoryDelta,
                ManagedAllocatedDeltaBytes: managedAllocDelta));
        }

        return new ExecutionProviderMatrixReport(
            Scenario: scenario,
            BaselineProvider: baselineProvider,
            Comparisons: comparisons,
            Timestamp: timestamp ?? DateTimeOffset.UtcNow,
            SkippedProviders: []);
    }

    /// <summary>
    /// Calculates comparison metrics from a dictionary of provider evidence reports.
    /// </summary>
    public static ExecutionProviderMatrixReport CompareProviders(
        string scenario,
        string baselineProvider,
        IReadOnlyDictionary<string, BenchmarkEvidenceReport> providerReports,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(providerReports);

        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>(
            StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();

        foreach ((string provider, BenchmarkEvidenceReport report) in providerReports)
        {
            // A run that failed or was skipped outright has no valid timing to compare.
            // Note: full-pipeline evidence reports are routinely "PartiallyCompleted" (the
            // runner has no per-stage ActualProvider to check against in that mode — see
            // ControlledDubbingBenchmarkRunner's full-pipeline status branch), so that status
            // alone must not disqualify a report; only a genuine provider fallback (detected via
            // RequestedProvider/ActualProvider, when both are known) does.
            bool fellBackToDifferentProvider = report.RequestedProvider is not null &&
                report.ActualProvider is not null &&
                !BenchmarkComparison.ProviderMatches(report.RequestedProvider, report.ActualProvider);
            bool didNotRun = report.Status is BenchmarkEvidenceStatus.Failed or BenchmarkEvidenceStatus.Skipped;
            if (didNotRun || fellBackToDifferentProvider)
            {
                skipped.Add(provider);
                continue;
            }

            double p50 = report.TimingsMilliseconds.GetValueOrDefault("pipeline:p50")
                ?? report.TimingsMilliseconds.GetValueOrDefault("pipeline")
                ?? 0.0;
            double throughput = report.TimingsMilliseconds.GetValueOrDefault("pipeline:throughput")
                ?? 0.0;
            long peakMemory = report.MemoryBytes.GetValueOrDefault("peakWorkingSetBytes")
                ?? report.MemoryBytes.GetValueOrDefault("processPeakWorkingSet")
                ?? 0L;
            long managedAlloc = report.MemoryBytes.GetValueOrDefault("managedAllocatedBytes")
                ?? 0L;

            stats[provider] = (p50, throughput, peakMemory, managedAlloc);
        }

        if (!stats.ContainsKey(baselineProvider) &&
            !stats.Keys.Any(k => BenchmarkComparison.ProviderMatches(k, baselineProvider)))
        {
            throw new ArgumentException(
                $"Baseline provider '{baselineProvider}' has no completed, on-target evidence to compare.",
                nameof(baselineProvider));
        }

        ExecutionProviderMatrixReport compared = CompareProviders(scenario, baselineProvider, stats, timestamp);
        return compared with { SkippedProviders = skipped };
    }

    /// <summary>
    /// Calculates comparison metrics from a collection of evidence reports.
    /// </summary>
    public static ExecutionProviderMatrixReport CompareProviders(
        string scenario,
        string baselineProvider,
        IReadOnlyList<BenchmarkEvidenceReport> reports,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var dict = new Dictionary<string, BenchmarkEvidenceReport>(StringComparer.OrdinalIgnoreCase);
        foreach (BenchmarkEvidenceReport report in reports)
        {
            string key = report.RequestedProvider ?? report.ActualProvider ?? "unknown";
            dict[key] = report;
        }

        return CompareProviders(scenario, baselineProvider, dict, timestamp);
    }

    /// <summary>
    /// Executes the comparative benchmark matrix across all configured execution providers.
    /// </summary>
    public async Task<ExecutionProviderMatrixReport> RunAsync(
        ExecutionProviderMatrixOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(options.OutputDirectory);
        var evidenceReports = new Dictionary<string, BenchmarkEvidenceReport>(StringComparer.OrdinalIgnoreCase);

        foreach (string provider in options.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string providerOutputDir = Path.Join(options.OutputDirectory, provider);
            Directory.CreateDirectory(providerOutputDir);

            Action<IServiceCollection>? configurator = null;
            if (options.Mock)
            {
                configurator = services => MockDubbingPipelineServices.ConfigureMockPipeline(services, mockOpts =>
                {
                    mockOpts.DefaultProvider = provider;
                    mockOpts.DryRun = options.DryRun;
                    double speedMultiplier = GetMockProviderSpeedMultiplier(provider);
                    mockOpts.SimulatedStageLatencies["audio-prep"] = TimeSpan.FromMilliseconds(10.0 * speedMultiplier);
                    mockOpts.SimulatedStageLatencies["separation"] = TimeSpan.FromMilliseconds(20.0 * speedMultiplier);
                    mockOpts.SimulatedStageLatencies["transcription"] = TimeSpan.FromMilliseconds(30.0 * speedMultiplier);
                    mockOpts.SimulatedStageLatencies["alignment"] = TimeSpan.FromMilliseconds(15.0 * speedMultiplier);
                    mockOpts.SimulatedStageLatencies["dubbing"] = TimeSpan.FromMilliseconds(25.0 * speedMultiplier);
                });
            }

            var runner = new ControlledDubbingBenchmarkRunner(serviceConfigurator: configurator);
            _disposables.Add(runner);

            BenchmarkEvidenceReport evidence = await runner.RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = options.FixturePath,
                    ExpectedFixtureSha256 = options.ExpectedFixtureSha256,
                    OutputDirectory = providerOutputDir,
                    Stage = options.Scenario.Equals("full-pipeline", StringComparison.OrdinalIgnoreCase) ? null : options.Scenario,
                    Provider = provider,
                    Mode = options.Mode,
                    ReuseEngineCache = options.ReuseEngineCache,
                    TargetLanguage = options.TargetLanguage,
                    SourceLanguage = options.SourceLanguage,
                    ModelDirectory = options.ModelDirectory,
                    FfmpegPath = options.FfmpegPath,
                    FfprobePath = options.FfprobePath,
                    RunCount = options.RunCount,
                    Mock = options.Mock,
                    DryRun = options.DryRun,
                },
                cancellationToken).ConfigureAwait(false);

            evidenceReports[provider] = evidence;
        }

        ExecutionProviderMatrixReport report = CompareProviders(
            options.Scenario,
            options.BaselineProvider,
            evidenceReports);

        string reportPath = Path.Join(options.OutputDirectory, "execution-provider-matrix.json");
        await BenchmarkReportWriter.WriteAsync(report, reportPath, cancellationToken).ConfigureAwait(false);

        return report;
    }

    private static double GetMockProviderSpeedMultiplier(string provider)
    {
        string normalized = BenchmarkComparison.NormalizeProvider(provider);
        return normalized switch
        {
            "directml" => 0.5,
            "tensorrt" or "tensorrtrtx" => 0.25,
            "openvino" => 0.6,
            "cpu" => 1.0,
            _ => 1.0,
        };
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            try { disposable.Dispose(); }
            catch (ObjectDisposedException ex) { System.Diagnostics.Trace.WriteLine($"Ignored during best-effort dispose: {ex}"); }
            catch (InvalidOperationException ex) { System.Diagnostics.Trace.WriteLine($"Ignored during best-effort dispose: {ex}"); }
        }
        _disposables.Clear();
    }
}
