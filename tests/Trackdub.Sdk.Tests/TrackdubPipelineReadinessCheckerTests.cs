using System.Text.Json;

using Trackdub.Cli;
using Trackdub.Cli.Handlers;
using Trackdub.Contracts.Pipeline;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubPipelineReadinessCheckerTests : IDisposable
{
    private readonly string _emptyModelDirectory = Path.Combine(
        Path.GetTempPath(),
        "TrackdubTests",
        Guid.NewGuid().ToString("N"),
        "models");

    public TrackdubPipelineReadinessCheckerTests()
    {
        Directory.CreateDirectory(_emptyModelDirectory);
    }

    [Fact]
    public async Task EvaluateDefaultPipelineAsync_WhenModelsMissing_ReportsBlockingStages()
    {
        using TrackdubSessionFactory factory = CreateFactoryWithoutModels();
        var checker = new TrackdubPipelineReadinessChecker(factory);

        PipelineReadinessReport report = await checker.EvaluateDefaultPipelineAsync();

        Assert.False(report.IsRunReady);
        Assert.NotEmpty(report.BlockingStages);
        Assert.Contains(report.Stages, stage => stage.Status == ReadinessState.DownloadRequired);
    }

    [Fact]
    public async Task CheckHandler_WhenModelsMissing_WritesNotReadyJsonAndExitCode2()
    {
        using TrackdubSessionFactory factory = CreateFactoryWithoutModels();
        using var output = new StringWriter();

        int exitCode = await CheckHandler.ExecuteAsync(factory, projectPath: null, output, CancellationToken.None);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("ready").GetBoolean());

        JsonElement stages = document.RootElement.GetProperty("stages");
        Assert.True(stages.GetArrayLength() > 0);
        Assert.Contains(
            stages.EnumerateArray(),
            element => element.GetProperty("readinessState").GetString() == "downloadRequired");
    }

    private TrackdubSessionFactory CreateFactoryWithoutModels() =>
        new TrackdubBuilder()
            .WithModelDirectory(_emptyModelDirectory)
            .WithModelCacheDirectory(_emptyModelDirectory)
            .Build();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_emptyModelDirectory))
            {
                Directory.Delete(Path.GetDirectoryName(_emptyModelDirectory)!, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
