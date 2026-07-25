using Spectre.Console;
using Spectre.Console.Testing;

using Trackdub.Cli;
using Trackdub.Cli.Interactive;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Sdk.Tests;

public sealed class CliProgressRunnerTests
{
    [Theory]
    [InlineData("json", true, false)]
    [InlineData("text", true, true)]
    [InlineData("text", false, false)]
    [InlineData("TEXT", true, true)]
    public void ShouldUseSpectreProgress_UsesTextOnInteractiveStderrOnly(
        string progressFormat,
        bool stderrInteractive,
        bool expected)
    {
        bool actual = CliProgressRunner.ShouldUseSpectreProgress(
            progressFormat,
            () => stderrInteractive);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ExecuteAsync_Json_ReportsJsonLinesToCapturedStderr()
    {
        var originalError = Console.Error;
        try
        {
            using var stderr = new StringWriter();
            Console.SetError(stderr);

            await CliProgressRunner.ExecuteAsync(
                "json",
                (progress, _) =>
                {
                    progress?.Report(new PipelineProgressEvent(
                        StageName: "ASR",
                        EventKind: PipelineProgressEventKind.Started,
                        Percentage: 0,
                        Message: null,
                        ElapsedDuration: TimeSpan.Zero));

                    return Task.FromResult(0);
                },
                CancellationToken.None,
                () => false);

            string output = stderr.ToString();
            Assert.Contains("\"stageName\":\"ASR\"", output, StringComparison.Ordinal);
            Assert.Contains("\"eventKind\":\"started\"", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Spectre_UpdatesProgressTasks()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;

        await console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .StartAsync(ctx =>
            {
                var reporter = new SpectreCliProgressReporter(ctx);
                reporter.Report(new PipelineProgressEvent(
                    StageName: "Export",
                    EventKind: PipelineProgressEventKind.Started,
                    Percentage: 0,
                    Message: null,
                    ElapsedDuration: TimeSpan.Zero));
                reporter.Report(new PipelineProgressEvent(
                    StageName: "Export",
                    EventKind: PipelineProgressEventKind.Progress,
                    PercentComplete: 42,
                    Message: "Muxing",
                    ElapsedDuration: TimeSpan.Zero,
                    Phase: "Muxing"));
                reporter.Report(new PipelineProgressEvent(
                    StageName: "Export",
                    EventKind: PipelineProgressEventKind.Completed,
                    Percentage: 100,
                    Message: null,
                    ElapsedDuration: TimeSpan.FromSeconds(3.2)));
                return Task.CompletedTask;
            });

        Assert.Contains("Export", console.Output, StringComparison.Ordinal);
    }
}
