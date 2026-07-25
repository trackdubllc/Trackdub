using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spectre.Console.Testing;

using Trackdub.Cli;
using Trackdub.Cli.Commands;
using Trackdub.Cli.Handlers;
using Trackdub.Cli.Tui;
using Trackdub.Cli.Tui.Screens;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;
using Trackdub.Sdk.Composition;
using Trackdub.TestDoubles;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubTuiTests : IDisposable
{
    private readonly string _emptyModelDirectory = Path.Combine(
        Path.GetTempPath(),
        "TrackdubTests",
        Guid.NewGuid().ToString("N"),
        "models");

    private readonly List<string> _tempDirs = [];

    public TrackdubTuiTests()
    {
        Directory.CreateDirectory(_emptyModelDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_emptyModelDirectory))
            {
                Directory.Delete(_emptyModelDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private TrackdubSessionFactory CreateFactory()
    {
        var options = new TrackdubOptions
        {
            ServiceConfigurator = services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IMediaProbe, FakeMediaProbe>());
                services.Replace(ServiceDescriptor.Singleton<IModelInventoryService>(
                    new FakeModelInventoryService()));
                services.Replace(ServiceDescriptor.Singleton<IStarterPackPresentationService>(
                    new FakeStarterPackPresentationService()));
                services.Replace(ServiceDescriptor.Singleton<IPipelineReadinessService>(
                    new FakePipelineReadinessService()));
            },
        };
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub(options);
        return new TrackdubSessionFactory(services.BuildServiceProvider());
    }

    private string CreateTempProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void BuildRootCommand_IncludesUiSubcommand()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);

        Command? uiCommand = rootCommand.Subcommands.FirstOrDefault(command =>
            string.Equals(command.Name, "ui", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(uiCommand);
    }

    [Fact]
    public async Task UiCommand_NonInteractive_ReturnsExitCode1()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse(["ui", "--model-directory", _emptyModelDirectory]);
        int exitCode = await parseResult.InvokeAsync();

        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public void RenderHeader_WithNumericTabLabels_DoesNotThrowMarkupException()
    {
        var console = new TestConsole();
        var renderHeader = typeof(TrackdubTuiApp).GetMethod(
            "RenderHeader",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(renderHeader);

        var screens = new Dictionary<TuiScreenId, ITuiScreen>
        {
            [TuiScreenId.Home] = new HomeTuiScreen(),
            [TuiScreenId.Models] = new ModelsTuiScreen(),
            [TuiScreenId.Project] = new ProjectTuiScreen(),
            [TuiScreenId.Pipeline] = new PipelineTuiScreen(),
            [TuiScreenId.Log] = new LogTuiScreen(),
        };

        Exception? exception = Record.Exception(() =>
            renderHeader.Invoke(null, [console, TuiScreenId.Models, screens]));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ModelsTuiScreen_RenderAsync_includes_pack_panel_columns()
    {
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        var context = new TrackdubTuiContext(factory, console, CancellationToken.None);
        var screen = new ModelsTuiScreen();

        await screen.RenderAsync(context);

        string output = console.Output;
        Assert.Contains("Required", output);
        Assert.Contains("Installed", output);
        Assert.Contains("Status", output);
        Assert.Contains("packs", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ad-hoc download", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelsTuiScreen_NeedsVoiceCloningConsentPrompt_respects_apply_readiness()
    {
        var grantedConsentSummary = new StarterPackSummary(
            "premium",
            "Premium / Quality",
            "quality",
            ["default"],
            RequiredCount: 1,
            InstalledCount: 1,
            CanApply: true,
            HasCommercialVerificationGap: false,
            RequiresVoiceCloningConsent: true,
            Recommended: false,
            Applied: false,
            BlockedReason: null,
            StatusLabel: string.Empty);

        StarterPackSummary missingConsentSummary = grantedConsentSummary with
        {
            CanApply = false,
            BlockedReason = "Voice cloning consent is required before applying this pack.",
        };

        Assert.False(ModelsTuiScreen.NeedsVoiceCloningConsentPrompt(grantedConsentSummary));
        Assert.True(ModelsTuiScreen.NeedsVoiceCloningConsentPrompt(missingConsentSummary));
    }

    [Fact]
    public void RenderFooter_ModelsScreen_mentions_packs_and_ad_hoc_download()
    {
        var console = new TestConsole();
        var renderFooter = typeof(TrackdubTuiApp).GetMethod(
            "RenderFooter",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(renderFooter);

        Exception? exception = Record.Exception(() =>
            renderFooter.Invoke(null, [console, TuiScreenId.Models]));

        Assert.Null(exception);
        string output = console.Output;
        Assert.Contains("packs", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ad-hoc download", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelsHandler_GetInventoryAsync_ReturnsManifestEntries()
    {
        TrackdubSessionFactory factory = new TrackdubBuilder()
            .WithModelDirectory(_emptyModelDirectory)
            .WithModelCacheDirectory(_emptyModelDirectory)
            .Build();

        using (factory)
        {
            var entries = await ModelsHandler.GetInventoryAsync(factory, CancellationToken.None);

            Assert.NotEmpty(entries);
            Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.ModelId)));
        }
    }

    [Fact]
    public void UiCommand_IsInteractiveTerminal_IsFalseWhenStderrRedirected()
    {
        var originalError = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            Assert.False(UiCommand.IsInteractiveTerminal());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task PipelineHandler_TryLoadSnapshot_ReturnsNullForMissingProject()
    {
        TrackdubSessionFactory factory = new TrackdubBuilder()
            .WithModelDirectory(_emptyModelDirectory)
            .WithModelCacheDirectory(_emptyModelDirectory)
            .Build();

        using (factory)
        {
            PipelineHandler.PipelineSnapshot? snapshot = await PipelineHandler.TryLoadSnapshotAsync(
                factory,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                CancellationToken.None);

            Assert.Null(snapshot);
        }
    }

    [Fact]
    public async Task PipelineHandler_TryLoadSnapshot_ReturnsStageRowsFromProjectDatabase()
    {
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Combine(tempDir, "sample.trackdub");
        Directory.CreateDirectory(projectDir);
        string mediaPath = Path.Combine(tempDir, "video.mp4");
        await File.WriteAllBytesAsync(mediaPath, [0x00, 0x00, 0x00, 0x20]);

        using TrackdubSessionFactory factory = CreateFactory();
        await using (TrackdubSession session = factory.CreateSession(projectDir))
        {
            await session.Workspace.CreateMediaSpineAsync(
                new CreateTranscriptProjectRequest("sample", mediaPath),
                CancellationToken.None);
        }

        PipelineHandler.PipelineSnapshot? snapshot = await PipelineHandler.TryLoadSnapshotAsync(
            factory,
            projectDir,
            CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("sample", snapshot!.ProjectName);
        Assert.Equal(mediaPath, snapshot.SourceMediaPath);
        Assert.Equal(PipelineHandler.UiStages.Length, snapshot.Stages.Count);
        Assert.All(snapshot.Stages, row => Assert.False(string.IsNullOrWhiteSpace(row.StageName)));
        Assert.Contains(snapshot.Stages, row => row.StageName == StageNames.Asr);
    }

    [Fact]
    public void TuiLogTail_ReadLastLines_ReturnsFinalLinesOnly()
    {
        string tempDir = CreateTempProjectDir();
        string logPath = Path.Combine(tempDir, "trackdub.log");
        File.WriteAllLines(logPath, Enumerable.Range(1, 50).Select(index => $"line-{index}"));

        IReadOnlyList<string> tail = TuiLogTail.ReadLastLines(logPath, lineCount: 5);

        Assert.Equal(5, tail.Count);
        Assert.Equal("line-46", tail[0]);
        Assert.Equal("line-50", tail[^1]);
    }

    [Fact]
    public async Task ProjectHandler_TryLoadDetail_ReturnsArtifactRowsForProject()
    {
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Combine(tempDir, "sample.trackdub");
        Directory.CreateDirectory(projectDir);
        string mediaPath = Path.Combine(tempDir, "video.mp4");
        await File.WriteAllBytesAsync(mediaPath, [0x00, 0x00, 0x00, 0x20]);

        using TrackdubSessionFactory factory = CreateFactory();
        await using (TrackdubSession session = factory.CreateSession(projectDir))
        {
            await session.Workspace.CreateMediaSpineAsync(
                new CreateTranscriptProjectRequest("sample", mediaPath),
                CancellationToken.None);
        }

        ProjectHandler.ProjectDetailSnapshot? detail = await ProjectHandler.TryLoadDetailAsync(
            factory,
            projectDir,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("sample", detail!.ProjectName);
        Assert.Equal(mediaPath, detail.SourceMediaPath);
    }

    private sealed class FakeModelInventoryService : IModelInventoryService
    {
        public Task<IReadOnlyList<ModelInventoryEntry>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelInventoryEntry>>([]);

        public Task<ModelInventoryEntry?> GetByModelIdAsync(
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInventoryEntry?>(null);
    }

    private sealed class FakeStarterPackPresentationService : IStarterPackPresentationService
    {
        private static readonly StarterPackSummary Summary = new(
            "basic",
            "Basic / Fast",
            "fast",
            ["default"],
            RequiredCount: 1,
            InstalledCount: 0,
            CanApply: false,
            HasCommercialVerificationGap: false,
            RequiresVoiceCloningConsent: false,
            Recommended: false,
            Applied: false,
            BlockedReason: "Download required.",
            StatusLabel: "download first");

        public Task<IReadOnlyList<StarterPackSummary>> ListSummariesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StarterPackSummary>>([Summary]);

        public Task<StarterPackSummary> GetSummaryAsync(
            string packId,
            string profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Summary);

        public Task<string?> GetRecommendedPackIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> RequiresVoiceCloningConsentAsync(
            string packId,
            string profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> GetRunnablePackIdsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakePipelineReadinessService : IPipelineReadinessService
    {
        public Task<PipelineReadinessReport> EvaluateAsync(
            IReadOnlyList<RuntimeStage> enabledStages,
            RuntimeModelSelections selections,
            TranscriptProjectState? state,
            CancellationToken cancellationToken = default,
            string? sourceLanguageCode = null,
            string? targetLanguageCode = null) =>
            Task.FromResult(PipelineReadinessReport.Empty);

        public void InvalidateCache(IReadOnlyList<RuntimeStage>? stages = null)
        {
        }
    }
}
