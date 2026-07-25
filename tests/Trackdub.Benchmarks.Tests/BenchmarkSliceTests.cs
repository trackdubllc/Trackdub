using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Trackdub.Benchmarks;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.TestDoubles;
#if WINDOWS
using Trackdub.Inference.Onnx;
using Microsoft.ML.OnnxRuntime;
#endif

namespace Trackdub.Benchmarks.Tests;

public sealed class BenchmarkOptionsTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [WindowsOnlyFact]
    public void TryParse_ParsesExpectedValues()
    {
        var args = new[]
        {
            "--model", ".\\model.onnx",
            "--output", ".\\out\\report.json",
            "--provider", "auto",
            "--runs", "7",
            "--format", "json"
        };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out var options);

        Assert.True(success);
        Assert.False(options.ShowHelp);
        Assert.Equal(BenchmarkProviderPreference.Auto, options.ProviderPreference);
        Assert.Equal(7, options.RunCount);
        Assert.Equal(ReportFormat.Json, options.ReportFormat);
        Assert.EndsWith(Path.Combine("out", "report.json"), options.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_ParsesTensorRtRtxProviderAlias()
    {
        var args = new[] { "--model", ".\\model.onnx", "--provider", "trt-rtx" };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out var options);

        Assert.True(success);
        Assert.Equal(BenchmarkProviderPreference.TensorRtRtx, options.ProviderPreference);
    }

    [Fact]
    public void TryParse_WhenWindowsMlDevicePolicyUnknown_ReturnsFalse()
    {
        var error = new StringWriter();
        string[] args =
        [
            "--model", "model.onnx",
            "--windows-ml-device-policy", "max-performnce"
        ];

        bool parsed = BenchmarkOptions.TryParse(args, error, out BenchmarkOptions? options);

        Assert.False(parsed);
        Assert.NotNull(options);
        Assert.True(options!.ShowHelp);
        Assert.Contains("Unknown Windows ML device policy", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WindowsMlExecutionDevicePolicySettings.ExplicitKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.MaxPerformanceKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.PreferNpuKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.MaxEfficiencyKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.MinOverallPowerKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.DefaultRenderKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.MinPowerKey)]
    public void TryParse_WhenWindowsMlDevicePolicyKnown_AcceptsKey(string policyKey)
    {
        var error = new StringWriter();
        string[] args =
        [
            "--model", "model.onnx",
            "--windows-ml-device-policy", policyKey
        ];

        bool parsed = BenchmarkOptions.TryParse(args, error, out BenchmarkOptions? options);

        Assert.True(parsed);
        Assert.NotNull(options);
        Assert.False(options!.ShowHelp);
        Assert.Equal(policyKey, options.WindowsMlDevicePolicyKey);
    }

    [Fact]
    public void TryParse_PreservesModelScopeReference()
    {
        var args = new[] { "--model", "silero-vad" };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out var options);

        Assert.True(success);
        Assert.Equal("silero-vad", options.ModelPath);
    }

    [Fact]
    public void TryParse_ParsesVariantOption()
    {
        var args = new[] { "--model", "silero-vad", "--variant", "q4" };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out var options);

        Assert.True(success);
        Assert.Equal("q4", options.Variant);
        Assert.False(options.AllVariants);
    }

    [Fact]
    public void TryParse_RejectsVariantAndAllVariants()
    {
        var args = new[] { "--model", "silero-vad", "--variant", "q4", "--all-variants" };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out _);

        Assert.False(success);
    }

#if WINDOWS
    [RequiresBundledModelFact("silero-vad/onnx/model_q4.onnx")]
    public async Task ProgramRunAsync_HonorsEmbeddedVariantReference()
    {
        string reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = await Program.RunAsync(
                ["--model", "silero-vad@q4", "--runs", "1", "--output", reportPath, "--format", "json"],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);

            await using FileStream reportStream = File.OpenRead(reportPath);
            BenchmarkReport? report = await JsonSerializer.DeserializeAsync<BenchmarkReport>(reportStream, SerializerOptions);
            Assert.NotNull(report);
            Assert.EndsWith(Path.Combine("models", "silero-vad", "onnx", "model_q4.onnx"), report!.ModelPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task ProgramRunAsync_PromptsForAmbiguousDirectoryAndStoresChoice()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"trackdub-bench-{Guid.NewGuid():N}");
        string onnxDirectory = Path.Combine(tempDirectory, "onnx");
        string variantAPath = Path.Combine(onnxDirectory, "variant_a.onnx");
        string variantBPath = Path.Combine(onnxDirectory, "variant_b.onnx");
        string reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        string defaultsPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        string? previousDefaultsPath = Environment.GetEnvironmentVariable("TRACKDUB_BENCHMARK_DEFAULTS_PATH");

        Directory.CreateDirectory(onnxDirectory);
        File.Copy(OnnxModelBenchmarkRunnerTests.SampleModelPath, variantAPath);
        File.Copy(OnnxModelBenchmarkRunnerTests.SampleModelPath, variantBPath);
        Environment.SetEnvironmentVariable("TRACKDUB_BENCHMARK_DEFAULTS_PATH", defaultsPath);

        try
        {
            var firstOutput = new StringWriter();
            var firstError = new StringWriter();

            int firstExitCode = await Program.RunAsync(
                ["--model", tempDirectory, "--runs", "1", "--output", reportPath, "--format", "json"],
                new StringReader("2" + Environment.NewLine),
                firstOutput,
                firstError,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, firstExitCode);
            Assert.Contains("Multiple benchmarkable ONNX variants were found", firstOutput.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(defaultsPath));
            string defaultsJson = await File.ReadAllTextAsync(defaultsPath);
            Assert.Contains("variant_b.onnx", defaultsJson, StringComparison.OrdinalIgnoreCase);

            var secondOutput = new StringWriter();
            var secondError = new StringWriter();
            int secondExitCode = await Program.RunAsync(
                ["--model", tempDirectory, "--runs", "1", "--output", reportPath, "--format", "json"],
                TextReader.Null,
                secondOutput,
                secondError,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, secondExitCode);
            Assert.DoesNotContain("Multiple benchmarkable ONNX variants were found", secondOutput.ToString(), StringComparison.Ordinal);

            await using FileStream reportStream = File.OpenRead(reportPath);
            BenchmarkReport? report = await JsonSerializer.DeserializeAsync<BenchmarkReport>(reportStream, SerializerOptions);
            Assert.NotNull(report);
            Assert.Equal(variantBPath, report!.ModelPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_BENCHMARK_DEFAULTS_PATH", previousDefaultsPath);
            File.Delete(reportPath);
            File.Delete(defaultsPath);
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task ProgramRunAsync_AllVariantsWritesAggregateAndPerVariantReports()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"trackdub-bench-{Guid.NewGuid():N}");
        string onnxDirectory = Path.Combine(tempDirectory, "onnx");
        string variantAPath = Path.Combine(onnxDirectory, "variant_a.onnx");
        string variantBPath = Path.Combine(onnxDirectory, "variant_b.onnx");
        string aggregateReportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        Directory.CreateDirectory(onnxDirectory);
        File.Copy(OnnxModelBenchmarkRunnerTests.SampleModelPath, variantAPath);
        File.Copy(OnnxModelBenchmarkRunnerTests.SampleModelPath, variantBPath);

        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = await Program.RunAsync(
                ["--model", tempDirectory, "--runs", "1", "--all-variants", "--output", aggregateReportPath, "--format", "json"],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(aggregateReportPath));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(aggregateReportPath)!, $"{Path.GetFileNameWithoutExtension(aggregateReportPath)}-variant_a{Path.GetExtension(aggregateReportPath)}")));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(aggregateReportPath)!, $"{Path.GetFileNameWithoutExtension(aggregateReportPath)}-variant_b{Path.GetExtension(aggregateReportPath)}")));
        }
        finally
        {
            File.Delete(aggregateReportPath);
            File.Delete(Path.Combine(Path.GetDirectoryName(aggregateReportPath)!, $"{Path.GetFileNameWithoutExtension(aggregateReportPath)}-variant_a{Path.GetExtension(aggregateReportPath)}"));
            File.Delete(Path.Combine(Path.GetDirectoryName(aggregateReportPath)!, $"{Path.GetFileNameWithoutExtension(aggregateReportPath)}-variant_b{Path.GetExtension(aggregateReportPath)}"));
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
#endif
}

#if WINDOWS
public sealed class OnnxModelBenchmarkRunnerTests
{
    internal static string SampleModelPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "models", "silero-vad", "onnx", "model.onnx"));

    private static string WhisperEncoderModelPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "models", "whisper-tiny-onnx", "onnx", "encoder_model.onnx"));

    private static string OpusEncoderModelPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "models", "opus", "Helsinki-NLP-opus-mt-en-es", "encoder_model.onnx"));

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task RunAsync_BuildsPlannedReportForExistingModel()
    {
        var modelPath = SampleModelPath;
        var reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(modelPath, reportPath, BenchmarkProviderPreference.Cpu, 3),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.Equal(3, report.RunCount);
            Assert.True(report.ModelSizeBytes > 0);
            Assert.NotNull(report.Measurements.ColdLoadMilliseconds);
            Assert.NotNull(report.Measurements.WarmupMilliseconds);
            Assert.NotNull(report.Measurements.WarmLatencyAverageMilliseconds);
            Assert.NotNull(report.Measurements.WarmLatencyMinimumMilliseconds);
            Assert.NotNull(report.Measurements.WarmLatencyMaximumMilliseconds);
            Assert.NotNull(report.Measurements.AudioDurationSeconds);
            Assert.NotNull(report.Measurements.RealTimeFactorAverage);
            Assert.True(report.Measurements.AudioDurationSeconds > 0);
            Assert.True(report.Measurements.RealTimeFactorAverage > 0);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task RunAsync_BuildsCompletedReportForManifestScopedSileroModel()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest("silero-vad", reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.EndsWith(Path.Combine("models", "silero-vad", "onnx", "model.onnx"), report.ModelPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(report.Notes, note => note.Contains("using manifest", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("whisper-tiny-onnx/onnx/encoder_model.onnx")]
    public async Task RunAsync_BuildsCompletedReportForWhisperEncoderModel()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(WhisperEncoderModelPath, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.Equal("whisper-encoder-decoder", report.Scenario);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.NotNull(report.Measurements.AudioDurationSeconds);
            Assert.NotNull(report.Measurements.RealTimeFactorAverage);
            Assert.True(report.Measurements.AudioDurationSeconds > 0);
            Assert.True(report.Measurements.RealTimeFactorAverage > 0);
            Assert.Contains(report.Notes, note => note.Contains("Whisper decoder discovered", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("opus/Helsinki-NLP-opus-mt-en-es/encoder_model.onnx")]
    public async Task RunAsync_BuildsCompletedReportForOpusEncoderModel()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(OpusEncoderModelPath, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.Equal("opus-mt-encoder-decoder", report.Scenario);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.Null(report.Measurements.AudioDurationSeconds);
            Assert.Null(report.Measurements.RealTimeFactorAverage);
            Assert.Contains(report.Notes, note => note.Contains("Opus decoder discovered", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("opus/Helsinki-NLP-opus-mt-en-es/encoder_model.onnx")]
    public async Task RunAsync_BuildsCompletedReportForManifestScopedOpusModel()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest("opus-en-es", reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.Equal("opus-mt-encoder-decoder", report.Scenario);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.EndsWith(
                Path.Combine("models", "opus", "Helsinki-NLP-opus-mt-en-es", "encoder_model.onnx"),
                report.ModelPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(report.Notes, note => note.Contains("using manifest", StringComparison.Ordinal));
            Assert.Contains(report.Notes, note => note.Contains("Opus decoder discovered", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task RunAsync_ResolvesSingleOnnxFileFromOnnxSubdirectory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"trackdub-bench-{Guid.NewGuid():N}");
        string onnxDirectory = Path.Combine(tempDirectory, "onnx");
        string copiedModelPath = Path.Combine(onnxDirectory, "variant_fp16.onnx");
        string reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        Directory.CreateDirectory(onnxDirectory);
        File.Copy(SampleModelPath, copiedModelPath);

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(tempDirectory, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.Equal(copiedModelPath, report.ModelPath);
            Assert.Contains(report.Notes, note => note.Contains("Resolved model directory", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [RequiresBundledModelTheory("silero-vad/onnx/model.onnx")]
    [InlineData(BenchmarkProviderPreference.Cpu, "cpu", "cpu")]
    [InlineData(BenchmarkProviderPreference.Dml, "dml", "dml")]
    [InlineData(BenchmarkProviderPreference.Auto, "auto", "auto")]
    public async Task RunAsync_UsesExplicitProviderRouting(
        BenchmarkProviderPreference preference,
        string expectedRequestedProvider,
        string expectedSelectedProvider)
    {
        var modelPath = SampleModelPath;
        var reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            BenchmarkReport report;

            try
            {
                report = await runner.RunAsync(
                    new BenchmarkRequest(modelPath, reportPath, preference, 1),
                    TestContext.Current.CancellationToken);
            }
            catch (Exception ex) when (
                preference is BenchmarkProviderPreference.Dml &&
                ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                return;
            }

            Assert.Equal(expectedRequestedProvider, report.RequestedProvider);
            if (preference is BenchmarkProviderPreference.Auto)
            {
                Assert.True(report.SelectedProvider is "cpu" or "dml");
            }
            else
            {
                Assert.Equal(expectedSelectedProvider, report.SelectedProvider);
            }
            Assert.Contains(report.Notes, note => note.Contains("Provider route:", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task RunAsync_RecordsWindowsMlBootstrapOutcomeForNonCpuProviders()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(SampleModelPath, reportPath, BenchmarkProviderPreference.Auto, 1),
                TestContext.Current.CancellationToken);

            Assert.Contains(
                report.Notes,
                note => note.Contains("Windows ML bootstrap", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }
}
#endif

public sealed class BenchmarkReportWriterTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Fact]
    public async Task WriteAsync_WritesJsonReport()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        var report = new BenchmarkReport(
            Scenario: "onnx-model",
            ModelPath: "model.onnx",
            ReportPath: reportPath,
            Status: BenchmarkStatus.Completed,
            RequestedProvider: "cpu",
            SelectedProvider: "cpu",
            RunCount: 5,
            SupportsExecution: true,
            ModelSizeBytes: 42,
            Measurements: new BenchmarkMeasurements(10, 5, 2, 1, 3, 0.032, 0.0625),
            FailureReason: null,
            Notes: ["slice wired"],
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        try
        {
            await BenchmarkReportWriter.WriteAsync(report, ReportFormat.Json, TestContext.Current.CancellationToken);

            await using var stream = File.OpenRead(reportPath);
            var written = await JsonSerializer.DeserializeAsync<BenchmarkReport>(stream, SerializerOptions);

            Assert.NotNull(written);
            Assert.Equal(report.Scenario, written.Scenario);
            Assert.Equal(report.ModelSizeBytes, written.ModelSizeBytes);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }
}

public sealed class BenchmarkConsoleTests
{
    [Fact]
    public void WriteSummary_IncludesRequestedAndSelectedProvider()
    {
        var report = new BenchmarkReport(
            Scenario: "onnx-model",
            ModelPath: "model.onnx",
            ReportPath: "report.json",
            Status: BenchmarkStatus.Planned,
            RequestedProvider: "auto",
            SelectedProvider: "cpu",
            RunCount: 5,
            SupportsExecution: true,
            ModelSizeBytes: 42,
            Measurements: new BenchmarkMeasurements(10, 5, 2, 1, 3, 0.032, 0.0625),
            FailureReason: null,
            Notes: [],
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        using var writer = new StringWriter();

        BenchmarkConsole.WriteSummary(report, writer);

        var summary = writer.ToString();
        Assert.Contains("Requested provider: auto", summary, StringComparison.Ordinal);
        Assert.Contains("Selected provider: cpu", summary, StringComparison.Ordinal);
        Assert.Contains("Cold load: 10.00 ms", summary, StringComparison.Ordinal);
        Assert.Contains("Warm latency avg/min/max: 2.00 ms / 1.00 ms / 3.00 ms", summary, StringComparison.Ordinal);
        Assert.Contains("Audio duration: 0.032 s", summary, StringComparison.Ordinal);
        Assert.Contains("Real-time factor: 0.062x", summary, StringComparison.Ordinal);
    }
}

public sealed class BenchmarkFailureTests
{
#if WINDOWS
    [Fact]
    public async Task RunAsync_ReturnsFailedReportForMissingModel()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        var reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(modelPath, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Failed, report.Status);
            Assert.False(report.SupportsExecution);
            Assert.NotNull(report.FailureReason);
            Assert.Contains("did not resolve", report.FailureReason, StringComparison.Ordinal);
            Assert.Null(report.Measurements.ColdLoadMilliseconds);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task RunAsync_ReturnsFailedReportForAmbiguousVariantDirectory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"trackdub-bench-{Guid.NewGuid():N}");
        string onnxDirectory = Path.Combine(tempDirectory, "onnx");
        string reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        Directory.CreateDirectory(onnxDirectory);
        File.WriteAllBytes(Path.Combine(onnxDirectory, "variant_a.onnx"), []);
        File.WriteAllBytes(Path.Combine(onnxDirectory, "variant_b.onnx"), []);

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(tempDirectory, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Failed, report.Status);
            Assert.False(report.SupportsExecution);
            Assert.NotNull(report.FailureReason);
            Assert.Contains("ambiguous", report.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("variant_a", report.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("variant_b", report.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(reportPath);
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
#endif

    [Fact]
    public void WriteSummary_IncludesFailureReason()
    {
        var report = new BenchmarkReport(
            Scenario: "onnx-model",
            ModelPath: "missing.onnx",
            ReportPath: "report.json",
            Status: BenchmarkStatus.Failed,
            RequestedProvider: "cpu",
            SelectedProvider: "cpu",
            RunCount: 1,
            SupportsExecution: false,
            ModelSizeBytes: 0,
            Measurements: new BenchmarkMeasurements(null, null, null, null, null, null, null),
            FailureReason: "Model path does not exist.",
            Notes: ["Benchmark failed: FileNotFoundException"],
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        using var writer = new StringWriter();
        BenchmarkConsole.WriteSummary(report, writer);
        var summary = writer.ToString();

        Assert.Contains("Failure reason: Model path does not exist.", summary, StringComparison.Ordinal);
    }
}

public sealed class BundledModelManifestRegistryTests
{
    [Fact]
    public void TryLoadDefault_ResolvesSileroAlias()
    {
        var success = BundledModelManifestRegistry.TryLoadDefault(out var registry, out var error);

        Assert.True(success, error);
        Assert.NotNull(registry);
        Assert.True(registry!.TryResolve("silero-vad", out var resolution));
        Assert.NotNull(resolution);
        Assert.Equal("silero-vad", resolution!.Alias);
        Assert.EndsWith(Path.Combine("models", "silero-vad", "onnx", "model.onnx"), resolution.EntryPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("MIT", resolution.Entry.License);
    }

    [Fact]
    public void TryLoadDefault_ResolvesNamedVariant()
    {
        var success = BundledModelManifestRegistry.TryLoadDefault(out var registry, out var error);

        Assert.True(success, error);
        Assert.NotNull(registry);
        Assert.True(registry!.TryResolve("silero-vad@q4", out var resolution));
        Assert.NotNull(resolution);
        Assert.Equal("q4", resolution!.VariantAlias);
        Assert.EndsWith(Path.Combine("models", "silero-vad", "onnx", "model_q4.onnx"), resolution.EntryPath, StringComparison.OrdinalIgnoreCase);
    }

#if WINDOWS
    [WindowsOnlyFact]
    public void CreateOnnxRunner_ReturnsRunnerOnWindowsTarget()
    {
        IModelBenchmarkRunner? runner = BenchmarkOnnxExecutionBootstrap.CreateOnnxRunner();

        Assert.NotNull(runner);
    }
#endif
}
