using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Benchmarks;

namespace Trackdub.Benchmarks.Tests;

public sealed class AudioPrepBenchmarkTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Fact]
    public async Task ProgramRunAsync_AudioPrepWritesReportAndAcceptsNonRegressingAuto()
    {
        string manifestPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        string reportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        var manifest = new AudioPrepBenchmarkManifest(
        [
            new AudioPrepBenchmarkFixture(
                "clean-vocal",
                FullMixPath: "mix.wav",
                VocalStemPath: "vocals.wav",
                ReferenceTranscript: "hello world",
                ReferenceSpeechCoverageSeconds: 2.0d,
                Results:
                [
                    new AudioPrepBenchmarkProfileInput(
                        "raw",
                        "hello world",
                        SpeechRegionCount: 1,
                        SpeechCoverageSeconds: 2.0d,
                        SpeakerCount: 1,
                        TurnCount: 1,
                        ProcessingSeconds: 0,
                        GuardrailFailures: []),
                    new AudioPrepBenchmarkProfileInput(
                        "auto",
                        "hello world",
                        SpeechRegionCount: 1,
                        SpeechCoverageSeconds: 2.0d,
                        SpeakerCount: 1,
                        TurnCount: 1,
                        ProcessingSeconds: 0.05d,
                        GuardrailFailures: [])
                ])
        ]);

        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, SerializerOptions));
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exitCode = await Program.RunAsync(
                ["audio-prep", "--manifest", manifestPath, "--output", reportPath, "--format", "json"],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(reportPath));
            await using FileStream stream = File.OpenRead(reportPath);
            AudioPrepBenchmarkReport? report = await JsonSerializer.DeserializeAsync<AudioPrepBenchmarkReport>(
                stream,
                SerializerOptions);
            Assert.NotNull(report);
            Assert.Equal(1, report!.Aggregate.AcceptedAutoCount);
            Assert.True(report.Fixtures[0].AutoComparison!.Accepted);
        }
        finally
        {
            File.Delete(manifestPath);
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task AudioPrepBenchmarkRunner_RejectsSpeakerFragmentationRegression()
    {
        var fixture = new AudioPrepBenchmarkFixture(
            "fragmented",
            FullMixPath: null,
            VocalStemPath: null,
            ReferenceTranscript: null,
            ReferenceSpeechCoverageSeconds: null,
            Results:
            [
                new AudioPrepBenchmarkProfileInput("raw", "hello", 2, 3.0d, 2, 10, 0, []),
                new AudioPrepBenchmarkProfileInput("auto", "hello", 3, 3.1d, 4, 14, 0.1d, [])
            ]);
        var runner = new AudioPrepBenchmarkRunner();
        var options = new AudioPrepBenchmarkOptions("unused", "unused", ReportFormat.Json, ShowHelp: false);

        // Exercise the public runner through a temporary manifest so JSON shape stays covered.
        string manifestPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new AudioPrepBenchmarkManifest([fixture]), SerializerOptions));
            AudioPrepBenchmarkReport report = await runner.RunAsync(options with { ManifestPath = manifestPath }, TestContext.Current.CancellationToken);

            Assert.False(report.Fixtures[0].AutoComparison!.Accepted);
            Assert.False(report.Fixtures[0].AutoComparison!.DiarizationFallbackAccepted);
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    [Fact]
    public void WriteAudioPrepSummary_FormatsWerAndCerDeltasAsRates()
    {
        var report = new AudioPrepBenchmarkReport(
            "manifest.json",
            "report.json",
            [
                new AudioPrepBenchmarkFixtureReport(
                    "fixture",
                    [],
                    new AudioPrepBenchmarkComparison(
                        WordErrorRateDelta: 0.02d,
                        CharacterErrorRateDelta: -0.01d,
                        SpeechCoverageDeltaSeconds: 0.5d,
                        SpeechRegionCountDelta: 0,
                        SpeakerCountDrift: 0,
                        TurnFragmentationIncreaseRatio: 1.2d,
                        DiarizationFallbackAccepted: true,
                        Accepted: true))
            ],
            new AudioPrepBenchmarkAggregate(
                FixtureCount: 1,
                AutoComparisonCount: 1,
                AcceptedAutoCount: 1,
                AverageWordErrorRateDelta: 0.02d,
                AverageCharacterErrorRateDelta: -0.01d,
                AverageSpeechCoverageDeltaSeconds: 0.5d,
                AverageTurnFragmentationIncreaseRatio: 1.2d),
            DateTimeOffset.UtcNow);
        var writer = new StringWriter();

        BenchmarkConsole.WriteAudioPrepSummary(report, writer);

        string output = writer.ToString();
        Assert.Contains("Average WER delta: +0.020", output, StringComparison.Ordinal);
        Assert.Contains("Average CER delta: -0.010", output, StringComparison.Ordinal);
        Assert.DoesNotContain("WER delta: 0.020x", output, StringComparison.Ordinal);
        Assert.DoesNotContain("CER delta: -0.010x", output, StringComparison.Ordinal);
    }
}
