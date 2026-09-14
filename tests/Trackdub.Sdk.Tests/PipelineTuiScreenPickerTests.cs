using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spectre.Console.Testing;

using Trackdub.Cli;
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

/// <summary>
/// Deterministic, offline coverage for the <see cref="PipelineTuiScreen"/> picker state machine.
/// Uses the injectable <c>IPipelineRunner</c> seam so terminal picker actions can be exercised
/// without a live pipeline run, and a Spectre.Console <see cref="TestConsole"/> to drive prompts.
/// </summary>
public sealed class PipelineTuiScreenPickerTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException ex)
            {
                Trace.WriteLine($"Failed to delete temp directory '{dir}' due to I/O error: {ex.Message}");
                Trace.WriteLine($"Failed to delete temp directory '{dir}' due to I/O error: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
                Trace.WriteLine($"Failed to delete temp directory '{dir}' due to access error: {ex.Message}");
            {
                Trace.WriteLine($"Failed to delete temp directory '{dir}' due to access error: {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // (1) Picker navigation: up/down movement and clamping
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StagePicker_NavigationClampsAtBothEnds()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        Assert.True(await screen.HandleKeyAsync(Key(ConsoleKey.S), context));
        Assert.True(screen.HasOverlay);

        TuiInlinePicker picker = GetPicker(screen);
        int choiceCount = picker.Choices.Count;
        Assert.Equal(0, picker.SelectedIndex);

        // UpArrow at the top clamps to 0.
        await screen.HandleKeyAsync(Key(ConsoleKey.UpArrow), context);
        Assert.Equal(0, picker.SelectedIndex);

        // DownArrow advances one at a time.
        await screen.HandleKeyAsync(Key(ConsoleKey.DownArrow), context);
        Assert.Equal(1, picker.SelectedIndex);

        // DownArrow past the end clamps to the final choice.
        for (int i = 0; i < choiceCount + 5; i++)
        {
            await screen.HandleKeyAsync(Key(ConsoleKey.DownArrow), context);
        }

        Assert.Equal(choiceCount - 1, picker.SelectedIndex);

        // UpArrow walks back and clamps at 0.
        for (int i = 0; i < choiceCount + 5; i++)
        {
            await screen.HandleKeyAsync(Key(ConsoleKey.UpArrow), context);
        }

        Assert.Equal(0, picker.SelectedIndex);
        Assert.Null(runner.LastStageName);
        Assert.Null(runner.LastFullPipelineOptions);
    }

    // -------------------------------------------------------------------------
    // (2) Back / cancel behaviour
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StagePicker_Escape_ClosesOverlayWithoutRunning()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.HandleKeyAsync(Key(ConsoleKey.S), context);
        Assert.True(screen.HasOverlay);

        await screen.HandleKeyAsync(Key(ConsoleKey.Escape), context);

        Assert.False(screen.HasOverlay);
        Assert.Equal(0, runner.RunStageCallCount);
        Assert.Equal(0, runner.RunFullPipelineCallCount);
    }

    [Fact]
    public async Task StagePicker_CKey_ClosesOverlayWithoutRunning()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.HandleKeyAsync(Key(ConsoleKey.S), context);
        Assert.True(screen.HasOverlay);

        await screen.HandleKeyAsync(Key(ConsoleKey.C), context);

        Assert.False(screen.HasOverlay);
        Assert.Equal(0, runner.RunStageCallCount);
        Assert.Equal(0, runner.RunFullPipelineCallCount);
    }

    [Fact]
    public async Task StageModelPicker_Back_ReturnsToStagePickerWithoutRunning()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.HandleKeyAsync(Key(ConsoleKey.S), context);
        Assert.Equal("Run which stage?", GetPicker(screen).Title);

        // Select the Transcribe (asr) stage → opens the model-alias picker.
        SelectValue(screen, StageNames.Asr);
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);
        Assert.True(screen.HasOverlay);
        Assert.Equal("Model alias override?", GetPicker(screen).Title);

        // The Back choice returns to the stage picker rather than firing a run.
        SelectValue(screen, "__back__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        Assert.True(screen.HasOverlay);
        Assert.Equal("Run which stage?", GetPicker(screen).Title);
        Assert.Equal(0, runner.RunStageCallCount);
    }

    [Fact]
    public async Task RunAll_Configure_DeepBackWalk_ReopensPriorPickersPreservingSelections()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("es"); // target language prompt

        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.RenderAsync(context);

        await screen.HandleKeyAsync(Key(ConsoleKey.G), context);
        SelectValue(screen, "__configure__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        // Walk forward into the wizard, making a distinct selection at each step so we can
        // confirm accumulated _w* state survives the Back walk.
        await SelectAndEnterAsync(screen, context, "__yes__");   // voice clone → true
        Assert.Equal("Export container format", GetPicker(screen).Title);
        await SelectAndEnterAsync(screen, context, "mkv");       // export format
        Assert.Equal("Subtitle format", GetPicker(screen).Title);
        await SelectAndEnterAsync(screen, context, "srt");       // subtitle format
        Assert.Equal("Subtitle transcript source", GetPicker(screen).Title);
        await SelectAndEnterAsync(screen, context, "bilingual"); // subtitle source
        Assert.Equal("Advanced options", GetPicker(screen).Title);

        // Descend into the video-encoder picker and set nvenc, then Back to Advanced.
        await SelectAndEnterAsync(screen, context, "__video__");
        Assert.Equal("Video encoder", GetPicker(screen).Title);
        await SelectAndEnterAsync(screen, context, "nvenc");
        Assert.Equal("Advanced options", GetPicker(screen).Title);

        // Now walk Back up several wizard pickers, asserting each prior picker re-opens by title.
        // Back transitions must NOT reset accumulated _w* state (only re-entering __configure__ does).
        await SelectAndEnterAsync(screen, context, "__back__"); // advanced → subtitle source
        Assert.Equal("Subtitle transcript source", GetPicker(screen).Title);
        await SelectAndEnterAsync(screen, context, "__back__"); // source → subtitle format
        Assert.Equal("Subtitle format", GetPicker(screen).Title);
        await SelectAndEnterAsync(screen, context, "__back__"); // format → export format
        Assert.Equal("Export container format", GetPicker(screen).Title);

        // No run fired anywhere along the Back walk.
        Assert.Equal(0, runner.RunFullPipelineCallCount);

        // Walk forward again straight to Run WITHOUT re-entering __configure__ (so no reset),
        // re-selecting the same values along the visible pickers. The video-encoder selection
        // (nvenc) made before the Back walk was never re-visited, so its survival to the fired
        // options proves the Back transitions preserved accumulated _w* state.
        await SelectAndEnterAsync(screen, context, "mkv");       // export format
        await SelectAndEnterAsync(screen, context, "srt");       // subtitle format
        await SelectAndEnterAsync(screen, context, "bilingual"); // subtitle source
        Assert.Equal("Advanced options", GetPicker(screen).Title);
        await SelectAndEnterAsync(screen, context, "__run__");   // advanced → fire

        Assert.Equal(1, runner.RunFullPipelineCallCount);
        PipelineHandler.TuiPipelineRunOptions options = runner.LastFullPipelineOptions!;
        Assert.True(options.UseVoiceCloning);                    // set before the Back walk, preserved
        Assert.Equal("mkv", options.ExportFormat);
        Assert.Equal(["srt"], options.SubtitleFormats!);
        Assert.Equal("bilingual", options.SubtitleSource);
        Assert.Equal("nvenc", options.VideoEncoderKey);          // set before the Back walk, preserved
        Assert.Equal("es", options.TargetLanguageOverride);
    }

    // -------------------------------------------------------------------------
    // (3) Alias forwarding to the runner
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StageRun_EnterAlias_ForwardsAliasAndStageToRunner()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("whisper-small");

        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.HandleKeyAsync(Key(ConsoleKey.S), context);
        SelectValue(screen, StageNames.Asr);
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        SelectValue(screen, "__enter_alias__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        Assert.Equal(1, runner.RunStageCallCount);
        Assert.Equal(StageNames.Asr, runner.LastStageName);
        Assert.Equal("whisper-small", runner.LastModelAlias);
        Assert.False(screen.HasOverlay);
    }

    [Fact]
    public async Task StageRun_DefaultModel_ForwardsNullAliasToRunner()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.HandleKeyAsync(Key(ConsoleKey.S), context);
        SelectValue(screen, StageNames.Asr);
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        SelectValue(screen, "__default__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        Assert.Equal(1, runner.RunStageCallCount);
        Assert.Equal(StageNames.Asr, runner.LastStageName);
        Assert.Null(runner.LastModelAlias);
        Assert.False(screen.HasOverlay);
    }

    [Fact]
    public async Task StageRun_EnterAlias_WhitespaceOnly_ForwardsNullAliasToRunner()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("   "); // whitespace-only alias → null

        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.HandleKeyAsync(Key(ConsoleKey.S), context);
        SelectValue(screen, StageNames.Asr);
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        SelectValue(screen, "__enter_alias__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        Assert.Equal(1, runner.RunStageCallCount);
        Assert.Equal(StageNames.Asr, runner.LastStageName);
        Assert.Null(runner.LastModelAlias);
        Assert.False(screen.HasOverlay);
    }

    // -------------------------------------------------------------------------
    // (4) Target-language prompting
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunAll_Configure_EmptyTargetLanguage_DoesNotRunAndSetsStatus()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter(string.Empty);

        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        // Render first so the (unset target language) snapshot is cached.
        await screen.RenderAsync(context);

        await screen.HandleKeyAsync(Key(ConsoleKey.G), context);
        SelectValue(screen, "__configure__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        Assert.Equal(0, runner.RunFullPipelineCallCount);
        Assert.Equal("Target language is required to run the pipeline.", context.StatusMessage);
        Assert.False(screen.HasOverlay);
    }

    [Fact]
    public async Task RunAll_Configure_ProvidedTargetLanguage_FlowsThroughToOptions()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("es");

        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.RenderAsync(context);

        await screen.HandleKeyAsync(Key(ConsoleKey.G), context);
        SelectValue(screen, "__configure__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        // Walk the wizard using defaults straight through to Run.
        await SelectAndEnterAsync(screen, context, "__no__");     // voice clone
        await SelectAndEnterAsync(screen, context, "__auto__");   // export format
        await SelectAndEnterAsync(screen, context, "__skip__");   // subtitle format
        await SelectAndEnterAsync(screen, context, "translated"); // subtitle source
        await SelectAndEnterAsync(screen, context, "__run__");    // advanced → fire

        Assert.Equal(1, runner.RunFullPipelineCallCount);
        Assert.NotNull(runner.LastFullPipelineOptions);
        Assert.Equal("es", runner.LastFullPipelineOptions!.TargetLanguageOverride);
    }

    // -------------------------------------------------------------------------
    // (5) Configured option mapping
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunAll_Configure_TogglesAndSelectionsMapToOptions()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("fr"); // target language prompt

        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.RenderAsync(context);

        await screen.HandleKeyAsync(Key(ConsoleKey.G), context);
        SelectValue(screen, "__configure__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        await SelectAndEnterAsync(screen, context, "__yes__");    // clone speaker voices
        await SelectAndEnterAsync(screen, context, "mkv");        // export format
        await SelectAndEnterAsync(screen, context, "srt");        // subtitle format → single element
        await SelectAndEnterAsync(screen, context, "bilingual");  // subtitle source

        // Advanced: flip timbre off (default true), pan on, loudness on; set video encoder nvenc.
        await SelectAndEnterAsync(screen, context, "__timbre__");
        await SelectAndEnterAsync(screen, context, "__pan__");
        await SelectAndEnterAsync(screen, context, "__loudness__");
        await SelectAndEnterAsync(screen, context, "__video__");
        await SelectAndEnterAsync(screen, context, "nvenc");
        await SelectAndEnterAsync(screen, context, "__run__");

        Assert.Equal(1, runner.RunFullPipelineCallCount);
        PipelineHandler.TuiPipelineRunOptions options = runner.LastFullPipelineOptions!;

        Assert.True(options.UseVoiceCloning);
        Assert.Equal("mkv", options.ExportFormat);
        Assert.NotNull(options.SubtitleFormats);
        Assert.Equal(["srt"], options.SubtitleFormats!);
        Assert.Equal("bilingual", options.SubtitleSource);
        Assert.Equal("nvenc", options.VideoEncoderKey);
        Assert.Equal("fr", options.TargetLanguageOverride);

        // Toggled fields.
        Assert.False(options.ApplyTimbrePolish); // default true, flipped off
        Assert.True(options.RestoreOriginalPan);
        Assert.True(options.MatchOriginalLoudness);

        // Preserved defaults for untouched fields.
        Assert.False(options.EnableAsrTextRefinement);
        Assert.False(options.BurnInSubtitles);
        Assert.False(options.ForceRerun);
    }

    [Fact]
    public async Task RunAll_Configure_VideoEncoderAuto_MapsToNull()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("de");

        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.RenderAsync(context);

        await screen.HandleKeyAsync(Key(ConsoleKey.G), context);
        SelectValue(screen, "__configure__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        await SelectAndEnterAsync(screen, context, "__no__");     // voice clone
        await SelectAndEnterAsync(screen, context, "__auto__");   // export format
        await SelectAndEnterAsync(screen, context, "__skip__");   // subtitle format
        await SelectAndEnterAsync(screen, context, "translated"); // subtitle source

        // Advanced: open the video encoder picker and explicitly select "auto".
        await SelectAndEnterAsync(screen, context, "__video__");
        await SelectAndEnterAsync(screen, context, "auto");       // "auto" → null mapping
        await SelectAndEnterAsync(screen, context, "__run__");    // fire

        Assert.Equal(1, runner.RunFullPipelineCallCount);
        PipelineHandler.TuiPipelineRunOptions options = runner.LastFullPipelineOptions!;
        Assert.Null(options.VideoEncoderKey);
    }

    [Fact]
    public async Task RunAll_Configure_SubtitleNone_MapsToEmptyList()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("de");

        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.RenderAsync(context);

        await screen.HandleKeyAsync(Key(ConsoleKey.G), context);
        SelectValue(screen, "__configure__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        await SelectAndEnterAsync(screen, context, "__no__");     // voice clone
        await SelectAndEnterAsync(screen, context, "__auto__");   // export format
        await SelectAndEnterAsync(screen, context, "__none__");   // subtitle format → empty list
        await SelectAndEnterAsync(screen, context, "translated"); // subtitle source
        await SelectAndEnterAsync(screen, context, "__run__");    // fire

        Assert.Equal(1, runner.RunFullPipelineCallCount);
        PipelineHandler.TuiPipelineRunOptions options = runner.LastFullPipelineOptions!;
        Assert.NotNull(options.SubtitleFormats);
        Assert.Empty(options.SubtitleFormats!);

        // The "translated" subtitle-source default maps to null (HandleSubtitleSourceChoiceAsync).
        Assert.Null(options.SubtitleSource);
    }

    [Fact]
    public async Task RunAll_Configure_SubtitleSkip_MapsToNull()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("de");

        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.RenderAsync(context);

        await screen.HandleKeyAsync(Key(ConsoleKey.G), context);
        SelectValue(screen, "__configure__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        await SelectAndEnterAsync(screen, context, "__no__");     // voice clone
        await SelectAndEnterAsync(screen, context, "__auto__");   // export format
        await SelectAndEnterAsync(screen, context, "__skip__");   // subtitle format → null
        await SelectAndEnterAsync(screen, context, "translated"); // subtitle source
        await SelectAndEnterAsync(screen, context, "__run__");    // fire

        Assert.Equal(1, runner.RunFullPipelineCallCount);
        PipelineHandler.TuiPipelineRunOptions options = runner.LastFullPipelineOptions!;
        Assert.Null(options.SubtitleFormats);
    }

    [Fact]
    public async Task RunAll_RunDefaults_FiresWithDefaultOptions()
    {
        var runner = new RecordingPipelineRunner();
        using TrackdubSessionFactory factory = CreateFactory();
        var console = new TestConsole();
        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = await CreateOpenProjectAsync(factory),
        };
        var screen = new PipelineTuiScreen(runner);

        await screen.HandleKeyAsync(Key(ConsoleKey.G), context);
        SelectValue(screen, "__run_defaults__");
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);

        Assert.Equal(1, runner.RunFullPipelineCallCount);
        PipelineHandler.TuiPipelineRunOptions options = runner.LastFullPipelineOptions!;
        Assert.True(options.ApplyTimbrePolish);
        Assert.False(options.UseVoiceCloning);
        Assert.Null(options.TargetLanguageOverride);
        Assert.False(screen.HasOverlay);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: false);

    private static TuiInlinePicker GetPicker(PipelineTuiScreen screen)
    {
        // Uses the internal test-only accessor (Trackdub.Cli exposes InternalsVisibleTo
        // Trackdub.Sdk.Tests) rather than reflecting into the private _picker field.
        TuiInlinePicker? picker = screen.CurrentPicker;
        Assert.NotNull(picker);
        return picker!;
    }

    private static void SelectValue(PipelineTuiScreen screen, string value)
    {
        TuiInlinePicker picker = GetPicker(screen);
        int index = -1;
        for (int i = 0; i < picker.Choices.Count; i++)
        {
            if (picker.Choices[i].Value == value)
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0, $"Choice '{value}' not present in picker '{picker.Title}'.");

        // Move the highlight to the target choice using only the public MoveUp/MoveDown API.
        while (picker.SelectedIndex < index)
        {
            picker.MoveDown();
        }

        while (picker.SelectedIndex > index)
        {
            picker.MoveUp();
        }
    }

    private static async Task SelectAndEnterAsync(
        PipelineTuiScreen screen,
        TrackdubTuiContext context,
        string value)
    {
        SelectValue(screen, value);
        await screen.HandleKeyAsync(Key(ConsoleKey.Enter), context);
    }

    private string CreateTempProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private async Task<string> CreateOpenProjectAsync(TrackdubSessionFactory factory)
    {
        string tempDir = CreateTempProjectDir();
        string projectDir = Path.Combine(tempDir, "sample.trackdub");
        Directory.CreateDirectory(projectDir);
        string mediaPath = Path.Combine(tempDir, "video.mp4");
        await File.WriteAllBytesAsync(mediaPath, [0x00, 0x00, 0x00, 0x20]);

        await using TrackdubSession session = factory.CreateSession(projectDir);
        await session.Workspace.CreateMediaSpineAsync(
            new CreateTranscriptProjectRequest("sample", mediaPath),
            CancellationToken.None);

        return projectDir;
    }

    private static TrackdubSessionFactory CreateFactory()
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

    /// <summary>
    /// Recording <c>IPipelineRunner</c> fake that captures the last run arguments and returns
    /// <see cref="Program.ExitSuccess"/> without ever touching a real pipeline.
    /// </summary>
    private sealed class RecordingPipelineRunner : IPipelineRunner
    {
        public int RunStageCallCount { get; private set; }

        public int RunFullPipelineCallCount { get; private set; }

        public string? LastStageName { get; private set; }

        public string? LastModelAlias { get; private set; }

        public PipelineHandler.TuiPipelineRunOptions? LastFullPipelineOptions { get; private set; }

        public Task<int> RunStageAsync(
            TrackdubSessionFactory factory,
            string projectPath,
            string stageName,
            string? modelAlias,
            CancellationToken cancellationToken)
        {
            RunStageCallCount++;
            LastStageName = stageName;
            LastModelAlias = modelAlias;
            return Task.FromResult(Program.ExitSuccess);
        }

        public Task<int> RunFullPipelineAsync(
            TrackdubSessionFactory factory,
            string projectPath,
            PipelineHandler.TuiPipelineRunOptions options,
            CancellationToken cancellationToken)
        {
            RunFullPipelineCallCount++;
            LastFullPipelineOptions = options;
            return Task.FromResult(Program.ExitSuccess);
        }
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
