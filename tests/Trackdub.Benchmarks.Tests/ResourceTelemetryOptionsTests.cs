using System.Globalization;
using Trackdub.Benchmarks.Tests.E2E.Fixtures;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks.Tests;

public sealed class ResourceTelemetryOptionsTests
{
    public static TheoryData<string, string> InvalidLimits
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string value in new[]
            {
                "-1", "100.0001", "NaN", "Infinity", "-Infinity", "1e309",
                "not-a-number", "1,5", "12,5", "1,000", "25%", "", " ",
            })
            {
                data.Add("--max-cpu-percent", value);
            }

            foreach (string option in new[]
            {
                "--max-working-set-bytes", "--max-allocated-bytes", "--min-available-vram-mb",
            })
            {
                foreach (string value in new[]
                {
                    "-1", "NaN", "Infinity", "-Infinity", "9223372036854775808",
                    "18446744073709551616", "1.5", "1,000", "1e3", "0x10", "bytes", "", " ",
                })
                {
                    data.Add(option, value);
                }
            }

            return data;
        }
    }

    public static TheoryData<string, string?> MissingLimits => new()
    {
        { "--max-cpu-percent", null },
        { "--max-working-set-bytes", null },
        { "--max-allocated-bytes", null },
        { "--min-available-vram-mb", null },
        { "--max-cpu-percent", "--mock" },
        { "--max-working-set-bytes", "--mock" },
        { "--max-allocated-bytes", "--mock" },
        { "--min-available-vram-mb", "--mock" },
    };

    [Fact]
    public void Matrix_parser_leaves_unspecified_limits_unbounded()
    {
        using var error = new StringWriter();

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            ["fixture.wav", "--output", "out"], error, out var options);

        Assert.True(parsed, error.ToString());
        Assert.NotNull(options);
        Assert.Equal(new ResourceTelemetryBounds(), options.ResourceTelemetryBounds);
    }

    [Fact]
    public void Matrix_parser_preserves_each_limit_exactly()
    {
        using var error = new StringWriter();

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            [
                "fixture.wav", "--output", "out",
                "--max-cpu-percent", "37.25",
                "--max-working-set-bytes", "4294967296",
                "--max-allocated-bytes", "123456789012345",
                "--min-available-vram-mb", "9223372036854775807",
            ], error, out var options);

        Assert.True(parsed, error.ToString());
        Assert.NotNull(options);
        Assert.Equal(new ResourceTelemetryBounds
        {
            MaxCpuPercent = 37.25,
            MaxWorkingSetBytes = 4294967296,
            MaxManagedAllocatedBytes = 123456789012345,
            MinAvailableVramMb = long.MaxValue,
        }, options.ResourceTelemetryBounds);
    }

    [Theory]
    [InlineData("0", "0", 0d, 0L)]
    [InlineData("100", "9223372036854775807", 100d, long.MaxValue)]
    [InlineData("2.5e1", "1", 25d, 1L)]
    public void Matrix_parser_accepts_inclusive_limits(
        string cpu, string bytes, double expectedCpu, long expectedBytes)
    {
        using var error = new StringWriter();

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            [
                "fixture.wav", "--output", "out",
                "--max-cpu-percent", cpu,
                "--max-working-set-bytes", bytes,
                "--max-allocated-bytes", bytes,
                "--min-available-vram-mb", bytes,
            ], error, out var options);

        Assert.True(parsed, error.ToString());
        Assert.NotNull(options);
        Assert.Equal(new ResourceTelemetryBounds
        {
            MaxCpuPercent = expectedCpu,
            MaxWorkingSetBytes = expectedBytes,
            MaxManagedAllocatedBytes = expectedBytes,
            MinAvailableVramMb = expectedBytes,
        }, options.ResourceTelemetryBounds);
    }

    [Theory]
    [InlineData("--max-cpu-percent")]
    [InlineData("--max-working-set-bytes")]
    [InlineData("--max-allocated-bytes")]
    [InlineData("--min-available-vram-mb")]
    public void Matrix_parser_sets_only_the_requested_limit(string option)
    {
        using var error = new StringWriter();
        var expected = option switch
        {
            "--max-cpu-percent" => new ResourceTelemetryBounds { MaxCpuPercent = 1 },
            "--max-working-set-bytes" => new ResourceTelemetryBounds { MaxWorkingSetBytes = 1 },
            "--max-allocated-bytes" => new ResourceTelemetryBounds { MaxManagedAllocatedBytes = 1 },
            _ => new ResourceTelemetryBounds { MinAvailableVramMb = 1 },
        };

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            ["fixture.wav", "--output", "out", option, "1"], error, out var options);

        Assert.True(parsed, error.ToString());
        Assert.NotNull(options);
        Assert.Equal(expected, options.ResourceTelemetryBounds);
    }

    [Theory]
    [MemberData(nameof(InvalidLimits))]
    public void Matrix_parser_rejects_invalid_limits_with_an_explicit_error(string option, string value)
    {
        using var error = new StringWriter();

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            ["fixture.wav", "--output", "out", option, value], error, out var options);

        Assert.False(parsed);
        Assert.Null(options);
        Assert.Contains("Invalid value", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(option, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MissingLimits))]
    public void Matrix_parser_rejects_missing_limits_with_an_explicit_error(string option, string? nextOption)
    {
        using var error = new StringWriter();
        string[] limitArgs = nextOption is null ? [option] : [option, nextOption];

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            ["fixture.wav", "--output", "out", .. limitArgs], error, out var options);

        Assert.False(parsed);
        Assert.Null(options);
        Assert.Contains($"Missing value for {option}", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    public void Matrix_parser_uses_invariant_culture(string cultureName)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            using var error = new StringWriter();

            bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
                [
                    "fixture.wav", "--output", "out",
                    "--max-cpu-percent", "25.5",
                    "--max-working-set-bytes", "4294967296",
                    "--max-allocated-bytes", "1024",
                    "--min-available-vram-mb", "2048",
                ], error, out var options);

            Assert.True(parsed, error.ToString());
            Assert.NotNull(options);
            Assert.Equal(new ResourceTelemetryBounds
            {
                MaxCpuPercent = 25.5,
                MaxWorkingSetBytes = 4294967296,
                MaxManagedAllocatedBytes = 1024,
                MinAvailableVramMb = 2048,
            }, options.ResourceTelemetryBounds);

            Assert.False(ControlledStageBenchmarkMatrixOptionsParser.TryParse(
                ["fixture.wav", "--output", "out", "--max-cpu-percent", "25,5"], error, out var invalidOptions));
            Assert.Null(invalidOptions);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task Matrix_runner_propagates_limits_to_every_stage_reportAsync()
    {
        using var harness = new MockDubbingBenchmarkHarness();
        string fixture = harness.CreateTempAudioFixture();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("trackdub-telemetry-options-");
        var bounds = new ResourceTelemetryBounds
        {
            MaxCpuPercent = 37.25,
            MaxWorkingSetBytes = 4294967296,
            MaxManagedAllocatedBytes = 123456789012345,
            MinAvailableVramMb = long.MaxValue,
        };

        try
        {
            using var runner = new ControlledStageBenchmarkMatrixRunner(
                new ControlledDubbingBenchmarkRunner(new NoHistory()));

            ControlledStageBenchmarkMatrixReport report = await runner.RunAsync(
                new ControlledStageBenchmarkMatrixOptions
                {
                    FixturePath = fixture,
                    OutputDirectory = directory.FullName,
                    Stages = ["audio-preparation", "asr"],
                    Mock = true,
                    ReuseEngineCache = true,
                    ResourceTelemetryBounds = bounds,
                }, TestContext.Current.CancellationToken);

            Assert.Equal(["audio-preparation", "asr"], report.Results.Select(result => result.Stage));
            Assert.All(report.Results, result => Assert.Equal(bounds, result.Evidence.ResourceTelemetryBounds));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidLimits))]
    public async Task Controlled_cli_rejects_invalid_limits_before_runningAsync(string option, string value)
    {
        using var error = new StringWriter();
        using var output = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["controlled", "fixture.wav", "--output", "out", option, value],
            TextReader.Null, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid value", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(option, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [MemberData(nameof(MissingLimits))]
    public async Task Controlled_cli_rejects_missing_limits_before_runningAsync(string option, string? nextOption)
    {
        using var error = new StringWriter();
        using var output = new StringWriter();
        string[] limitArgs = nextOption is null ? [option] : [option, nextOption];

        int exitCode = await Program.RunAsync(
            ["controlled", "fixture.wav", "--output", "out", .. limitArgs],
            TextReader.Null, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains($"Missing value for {option}", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [InlineData("controlled")]
    [InlineData("controlled-matrix")]
    public async Task Cli_help_describes_resource_limitsAsync(string command)
    {
        using var error = new StringWriter();
        using var output = new StringWriter();

        int exitCode = await Program.RunAsync(
            [command, "--help"], TextReader.Null, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("--max-cpu-percent", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--max-working-set-bytes", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--max-allocated-bytes", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--min-available-vram-mb", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("0..100", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class NoHistory : IBenchmarkEvidenceRepository
    {
        public Task SaveAsync(BenchmarkEvidenceReport report, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<BenchmarkEvidenceReport?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BenchmarkEvidenceReport?>(null);

        public Task<IReadOnlyList<BenchmarkEvidenceReport>> ListRecentAsync(
            BenchmarkEvidenceKind? kind, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkEvidenceReport>>([]);
    }
}
