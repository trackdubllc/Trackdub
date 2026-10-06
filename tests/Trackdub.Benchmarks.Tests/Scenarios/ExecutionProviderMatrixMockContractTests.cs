using System.Text.Json;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Scenarios;

namespace Trackdub.Benchmarks.Tests.Scenarios;

/// <summary>
/// The contract a mock provider-matrix run is built on. A mock comparison demonstrates these
/// simulated speedups and nothing more: the wall-clock ratios it measures are dominated by each
/// provider's own fixed run cost, so tests assert the contract a mock run carries on its comparison
/// rows rather than the measured ratios.
/// </summary>
public sealed class ExecutionProviderMatrixMockContractTests
{
    [Theory]
    [InlineData("cpu", 1.0, 1000.0)]
    [InlineData("directml", 0.5, 500.0)]
    [InlineData("dml", 0.5, 500.0)]             // CLI alias for directml
    [InlineData("tensorrt", 0.25, 250.0)]
    [InlineData("tensorrt-rtx", 0.25, 250.0)]   // normalized to tensorrtrtx
    [InlineData("openvino", 0.6, 600.0)]
    [InlineData("unlisted", 1.0, 1000.0)]       // a provider with no simulated speed runs at baseline
    public void Simulated_latency_multiplier_and_budget_match_the_contract(
        string provider, double expectedMultiplier, double expectedBudgetMilliseconds)
    {
        Assert.Equal(
            expectedMultiplier,
            ExecutionProviderMatrixRunner.SimulatedLatencyMultiplier(provider),
            precision: 9);
        Assert.Equal(
            expectedBudgetMilliseconds,
            ExecutionProviderMatrixRunner.SimulatedLatencyBudgetMilliseconds(provider),
            precision: 9);
    }

    [Theory]
    [InlineData("directml", "cpu", 2.0)]
    [InlineData("tensorrt", "cpu", 4.0)]
    [InlineData("dml", "cpu", 2.0)]
    [InlineData("cpu", "directml", 0.5)]
    [InlineData("cpu", "tensorrt", 0.25)]
    [InlineData("cpu", "cpu", 1.0)]
    public void Simulated_speedup_is_the_documented_ratio_over_the_baseline(
        string provider, string baselineProvider, double expectedSpeedup)
    {
        Assert.Equal(
            expectedSpeedup,
            ExecutionProviderMatrixRunner.SimulatedSpeedupFactor(provider, baselineProvider),
            precision: 9);
    }

    // ── The contract travels on the report ───────────────────────────────────────────────────

    [Fact]
    public void Comparison_rows_carry_the_simulated_budget_a_deterministic_run_supplies()
    {
        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "full-pipeline",
            baselineProvider: "cpu",
            providerStats: Stats(),
            simulatedLatencyBudgets: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["cpu"] = 1000.0,
                ["directml"] = 500.0,
                ["tensorrt"] = 250.0,
            });

        Assert.Equal(1000.0, Budget(report, "cpu"));
        Assert.Equal(500.0, Budget(report, "directml"));
        Assert.Equal(250.0, Budget(report, "tensorrt"));
    }

    [Fact]
    public void Budgets_match_rows_through_the_same_alias_tolerance_as_the_baseline()
    {
        // A caller may key budgets by a provider name the compared statistics spell differently,
        // so a row must still find its budget by normalized name.
        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "full-pipeline",
            baselineProvider: "cpu",
            providerStats: Stats(),
            simulatedLatencyBudgets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["CPU"] = 1000.0,
                ["dml"] = 500.0,
            });

        Assert.Equal(1000.0, Budget(report, "cpu"));
        Assert.Equal(500.0, Budget(report, "directml"));
        // No budget names tensorrt, so its row reports none rather than borrowing another's.
        Assert.Null(Budget(report, "tensorrt"));
    }

    [Theory]
    [InlineData("full-pipeline", "cpu", 1000.0)]
    [InlineData("full-pipeline", "directml", 500.0)]
    [InlineData("transcription", "cpu", 300.0)]
    [InlineData("asr", "cpu", 300.0)]               // alias for the transcription stage
    [InlineData("separation", "directml", 100.0)]   // 200 ms at the 0.5 DirectML multiplier
    [InlineData("tts", "tensorrt", 62.5)]           // 250 ms dubbing at the 0.25 TensorRT multiplier
    [InlineData("audio-prep", "cpu", 100.0)]
    [InlineData("unknown-stage", "cpu", 5.0)]       // mock fallback delay, unscaled
    [InlineData(null, "cpu", 1000.0)]
    public void Simulated_latency_budget_follows_the_selected_scenario(
        string? scenario, string provider, double expectedBudgetMilliseconds)
    {
        Assert.Equal(
            expectedBudgetMilliseconds,
            ExecutionProviderMatrixRunner.SimulatedLatencyBudgetMilliseconds(provider, scenario),
            precision: 9);
    }

    [Fact]
    public void Comparison_rows_carry_no_simulated_budget_without_one()
    {
        // Real execution simulates nothing, so its rows must not claim a budget.
        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "full-pipeline",
            baselineProvider: "cpu",
            providerStats: Stats());

        Assert.All(report.Comparisons, comparison => Assert.Null(comparison.SimulatedLatencyBudgetMilliseconds));
    }

    [Fact]
    public void Serialized_rows_omit_the_budget_when_absent_so_real_reports_keep_their_shape()
    {
        ExecutionProviderMatrixReport real = ExecutionProviderMatrixRunner.CompareProviders(
            "full-pipeline", "cpu", Stats());
        string realJson = JsonSerializer.Serialize(real, BenchmarkReportWriter.SerializerOptions);
        Assert.DoesNotContain("SimulatedLatencyBudgetMilliseconds", realJson, StringComparison.Ordinal);

        ExecutionProviderMatrixReport simulated = ExecutionProviderMatrixRunner.CompareProviders(
            "full-pipeline",
            "cpu",
            Stats(),
            simulatedLatencyBudgets: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["cpu"] = 1000.0,
                ["directml"] = 500.0,
            });
        string simulatedJson = JsonSerializer.Serialize(simulated, BenchmarkReportWriter.SerializerOptions);
        Assert.Contains("\"SimulatedLatencyBudgetMilliseconds\": 1000", simulatedJson, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        Stats() => new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 1000.0, Throughput: 1.0, PeakMemory: 0, ManagedAlloc: 0),
            ["directml"] = (P50: 500.0, Throughput: 2.0, PeakMemory: 0, ManagedAlloc: 0),
            ["tensorrt"] = (P50: 250.0, Throughput: 4.0, PeakMemory: 0, ManagedAlloc: 0),
        };

    private static double? Budget(ExecutionProviderMatrixReport report, string provider) =>
        report.Comparisons.First(comparison => comparison.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .SimulatedLatencyBudgetMilliseconds;
}
