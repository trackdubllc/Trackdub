using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

using Trackdub.Cli;
using Trackdub.Cli.Handlers;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts;
using Trackdub.TestDoubles;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubPipelineReadinessCheckerTests : IDisposable
{
    private readonly string _emptyModelDirectory = Path.Join(
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
        Assert.All(report.Stages.Where(stage => stage.Status == ReadinessState.DownloadRequired),
            stage => Assert.Equal("bundle-needed", stage.ResolveAction));
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
        Assert.All(stages.EnumerateArray().Where(element => element.GetProperty("readinessState").GetString() == "downloadRequired"),
            element => Assert.Equal("bundle-needed", element.GetProperty("resolveAction").GetString()));
    }

    private TrackdubSessionFactory CreateFactoryWithoutModels() =>
        new TrackdubBuilder()
            .WithModelDirectory(_emptyModelDirectory)
            .WithModelCacheDirectory(_emptyModelDirectory)
            .ConfigureServices(services => services.AddSingleton<IStudioSettingsService>(new FakeStudioSettingsService()))
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
