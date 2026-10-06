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
    /// <param name="simulatedLatencyBudgets">
    /// The simulated latency budget, in milliseconds, a deterministic mock run configured for each
    /// provider, keyed by provider name. Matching rows carry it as
    /// <see cref="ProviderComparisonMetrics.SimulatedLatencyBudgetMilliseconds"/>; a null map, or a
    /// provider absent from it, leaves the row's budget null. See
    /// <see cref="SimulatedLatencyBudgetMilliseconds"/> for what the budget means.
    /// </param>
    /// <returns>A complete <see cref="ExecutionProviderMatrixReport"/> with comparative metrics.</returns>
    /// <exception cref="ArgumentException">Thrown when scenario or baselineProvider is empty, or baselineProvider is not in providerStats.</exception>
    public static ExecutionProviderMatrixReport CompareProviders(
        string scenario,
        string baselineProvider,
        IReadOnlyDictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)> providerStats,
        DateTimeOffset? timestamp = null,
        IReadOnlyDictionary<string, double>? simulatedLatencyBudgets = null)
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
                ManagedAllocatedDeltaBytes: managedAllocDelta)
            {
                SimulatedLatencyBudgetMilliseconds = LookupSimulatedLatencyBudget(provider, simulatedLatencyBudgets),
            });
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
    /// <param name="simulatedLatencyBudgets">
    /// Optional per-provider simulated latency budgets, attached to the matching comparison rows.
    /// See the statistics overload for the matching rules.
    /// </param>
    public static ExecutionProviderMatrixReport CompareProviders(
        string scenario,
        string baselineProvider,
        IReadOnlyDictionary<string, BenchmarkEvidenceReport> providerReports,
        DateTimeOffset? timestamp = null,
        IReadOnlyDictionary<string, double>? simulatedLatencyBudgets = null)
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
            bool didNotRun = report.Status is BenchmarkEvidenceStatus.Failed or BenchmarkEvidenceStatus.Skipped
                or BenchmarkEvidenceStatus.Canceled;
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

        ExecutionProviderMatrixReport compared = CompareProviders(
            scenario, baselineProvider, stats, timestamp, simulatedLatencyBudgets);
        return compared with { SkippedProviders = skipped };
    }

    /// <summary>
    /// Calculates comparison metrics from a collection of evidence reports.
    /// </summary>
    /// <param name="simulatedLatencyBudgets">
    /// Optional per-provider simulated latency budgets, attached to the matching comparison rows.
    /// See the statistics overload for the matching rules.
    /// </param>
    public static ExecutionProviderMatrixReport CompareProviders(
        string scenario,
        string baselineProvider,
        IReadOnlyList<BenchmarkEvidenceReport> reports,
        DateTimeOffset? timestamp = null,
        IReadOnlyDictionary<string, double>? simulatedLatencyBudgets = null)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var dict = new Dictionary<string, BenchmarkEvidenceReport>(StringComparer.OrdinalIgnoreCase);
        foreach (BenchmarkEvidenceReport report in reports)
        {
            string key = report.RequestedProvider ?? report.ActualProvider ?? "unknown";
            dict[key] = report;
        }

        return CompareProviders(scenario, baselineProvider, dict, timestamp, simulatedLatencyBudgets);
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
                    double latencyMultiplier = SimulatedLatencyMultiplier(provider);
                    // The base delays are large enough (sum 1s at 1.0x) that a provider's simulated
                    // speed survives OS scheduler jitter; see SimulatedStageDelayMilliseconds for
                    // what a mock run's measurement is allowed to be asserted against.
                    foreach ((string stage, double milliseconds) in SimulatedStageDelayTable)
                    {
                        mockOpts.SimulatedStageLatencies[stage] =
                            TimeSpan.FromMilliseconds(milliseconds * latencyMultiplier);
                    }
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

        // A mock run that actually waits its simulated delays is the deterministic mode: report
        // each provider's budget so a caller can assert the simulated contract from the report
        // alone. A dry run waits nothing, and real execution simulates nothing, so neither carries
        // a budget.
        IReadOnlyDictionary<string, double>? simulatedLatencyBudgets = null;
        if (options.Mock && !options.DryRun)
        {
            var budgets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string provider in options.Providers)
            {
                budgets[provider] = SimulatedLatencyBudgetMilliseconds(provider, options.Scenario);
            }

            simulatedLatencyBudgets = budgets;
        }

        ExecutionProviderMatrixReport report = CompareProviders(
            options.Scenario,
            options.BaselineProvider,
            evidenceReports,
            simulatedLatencyBudgets: simulatedLatencyBudgets);

        string reportPath = Path.Join(options.OutputDirectory, "execution-provider-matrix.json");
        await BenchmarkReportWriter.WriteAsync(report, reportPath, cancellationToken).ConfigureAwait(false);

        return report;
    }

    /// <summary>
    /// Base simulated delay of each canonical stage, in milliseconds, before a provider's
    /// <see cref="SimulatedLatencyMultiplier"/> scales it.
    /// </summary>
    private static readonly (string Stage, double Milliseconds)[] SimulatedStageDelayTable =
    [
        ("audio-prep", 100.0),
        ("separation", 200.0),
        ("transcription", 300.0),
        ("alignment", 150.0),
        ("dubbing", 250.0),
    ];

    /// <summary>
    /// The factor a mock matrix run applies to every simulated stage delay for
    /// <paramref name="provider"/>: a provider at 0.5 runs the same stages in half the time of the
    /// 1.0 baseline, so the baseline is twice as slow as it.
    /// </summary>
    /// <remarks>
    /// A mock comparison demonstrates this contract and nothing more. The fixed cost of each
    /// provider's own run — host setup, resource telemetry sampling, the simulated allocations —
    /// is of the same order as the simulated gap, so a measured pipeline percentile cannot assert
    /// the simulated ratio: measured TensorRT routinely exceeds measured DirectML even though its
    /// simulated stages take half as long. Callers that need to state what a mock run guarantees
    /// should use <see cref="SimulatedSpeedupFactor"/> and
    /// <see cref="SimulatedLatencyBudgetMilliseconds"/> instead of the measured ratios.
    /// </remarks>
    internal static double SimulatedLatencyMultiplier(string provider)
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

    /// <summary>
    /// The speedup <paramref name="provider"/>'s simulated stages have over
    /// <paramref name="baselineProvider"/>'s, i.e. the ratio the mock's configured stage delays
    /// imply: DirectML is 2x and TensorRT 4x the CPU baseline.
    /// </summary>
    internal static double SimulatedSpeedupFactor(string provider, string baselineProvider) =>
        SimulatedLatencyMultiplier(baselineProvider) / SimulatedLatencyMultiplier(provider);

    /// <summary>
    /// Simulated stage latency, in milliseconds, a mock matrix run configures for
    /// <paramref name="provider"/> over the stages <paramref name="scenario"/> selects (1000 ms
    /// for the full pipeline at a multiplier of 1.0). Every simulated stage the run executes
    /// waits at least its configured delay, so a completed mock pipeline's measured percentile
    /// cannot be below this budget, and the ratio between two providers' budgets is the speedup
    /// the simulation is built to demonstrate. Mock matrix runs carry it on each comparison row as
    /// <see cref="ProviderComparisonMetrics.SimulatedLatencyBudgetMilliseconds"/>, so consumers can
    /// assert the contract from the report instead of from here.
    /// </summary>
    /// <param name="provider">The compared provider whose multiplier scales the delays.</param>
    /// <param name="scenario">
    /// The scenario identifier the run passes as the pipeline's stage filter — a single stage
    /// (possibly an alias such as <c>asr</c> or <c>tts</c>) or <c>full-pipeline</c>. A single-stage
    /// scenario only ever waits its own canonical stage's delay, so charging it the whole
    /// pipeline's sum would report a budget no run of it measures against. Null or
    /// <c>full-pipeline</c> budgets every stage, matching a run whose filter is unset.
    /// </param>
    internal static double SimulatedLatencyBudgetMilliseconds(string provider, string? scenario = null) =>
        SimulatedStageDelayTable
            .Where(stage => ScenarioSelectsStage(scenario, stage.Stage))
            .Sum(stage => stage.Milliseconds) * SimulatedLatencyMultiplier(provider);

    /// <summary>
    /// Whether a run with <paramref name="scenario"/>'s stage filter executes
    /// <paramref name="stage"/>: the same canonical-name comparison the mock pipeline applies, so
    /// the budget and the waits a run actually performs cannot drift apart.
    /// </summary>
    private static bool ScenarioSelectsStage(string? scenario, string stage)
    {
        if (string.IsNullOrWhiteSpace(scenario) ||
            scenario.Equals("full-pipeline", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return MockDubbingPipelineServices.CanonicalBenchmarkStage(scenario)
            .Equals(MockDubbingPipelineServices.CanonicalBenchmarkStage(stage), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The simulated latency budget recorded for <paramref name="provider"/>, or
    /// <see langword="null"/> when no budget map was supplied or none of its entries names the
    /// provider. Budgets are keyed by the names the caller configured, which need not spell a
    /// provider the way the compared statistics do (<c>dml</c> against <c>directml</c>), so an
    /// exact match is tried before a normalized one — the same tolerance the baseline lookup uses.
    /// </summary>
    private static double? LookupSimulatedLatencyBudget(
        string provider, IReadOnlyDictionary<string, double>? simulatedLatencyBudgets)
    {
        if (simulatedLatencyBudgets is null)
        {
            return null;
        }

        if (simulatedLatencyBudgets.TryGetValue(provider, out double exact))
        {
            return exact;
        }

        string normalized = BenchmarkComparison.NormalizeProvider(provider);
        foreach ((string candidate, double budget) in simulatedLatencyBudgets)
        {
            if (BenchmarkComparison.NormalizeProvider(candidate).Equals(
                    normalized, StringComparison.OrdinalIgnoreCase))
            {
                return budget;
            }
        }

        return null;
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
