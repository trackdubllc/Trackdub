using Spectre.Console.Testing;

using Trackdub.Cli;
using Trackdub.Cli.Interactive;

namespace Trackdub.Sdk.Tests;

public sealed class DubSetupWizardTests
{
    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenRequiredValuesAreMissing()
    {
        var request = new DubSetupRequest(
            MediaPath: null,
            TargetLanguage: null,
            SourceLanguage: null,
            OutputDirectory: null,
            ModelOverrides: [],
            ExportFormat: null);

        Assert.True(DubSetupWizard.RequiresSetup(request));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenRequiredValuesArePresent()
    {
        var request = new DubSetupRequest(
            MediaPath: "source.mp4",
            TargetLanguage: "es",
            SourceLanguage: null,
            OutputDirectory: null,
            ModelOverrides: [],
            ExportFormat: null);

        Assert.False(DubSetupWizard.RequiresSetup(request));
    }

    [Fact]
    public async Task CompleteAsync_CollectsRequiredValuesAndOptionalPreferences()
    {
        using var input = new StringReader(
            string.Join(
                Environment.NewLine,
                [
                    "source.mp4",
                    "fr",
                    "en",
                    "out.trackdub",
                    "asr:large-v3",
                    "tts:xtts-v2",
                    "",
                    "mkv",
                ]));
        using var output = new StringWriter();

        var request = new DubSetupRequest(
            MediaPath: null,
            TargetLanguage: null,
            SourceLanguage: null,
            OutputDirectory: null,
            ModelOverrides: [],
            ExportFormat: null);

        DubSetupRequest? result = await DubSetupWizard.CompleteAsync(request, input, output, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("source.mp4", result.MediaPath);
        Assert.Equal("fr", result.TargetLanguage);
        Assert.Equal("en", result.SourceLanguage);
        Assert.Equal("out.trackdub", result.OutputDirectory);
        Assert.Equal(["asr:large-v3", "tts:xtts-v2"], result.ModelOverrides);
        Assert.Equal("mkv", result.ExportFormat);

        string prompts = output.ToString();
        Assert.Contains("Setup stage 1/5", prompts);
        Assert.DoesNotContain("commercial-safe", prompts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_RePromptsMalformedModelOverride()
    {
        using var input = new StringReader(
            string.Join(
                Environment.NewLine,
                [
                    "",
                    "",
                    "not-a-stage-override",
                    "translation:madlad",
                    "",
                    "",
                ]));
        using var output = new StringWriter();

        var request = new DubSetupRequest(
            MediaPath: "source.mp4",
            TargetLanguage: "es",
            SourceLanguage: null,
            OutputDirectory: null,
            ModelOverrides: [],
            ExportFormat: null);

        DubSetupRequest? result = await DubSetupWizard.CompleteAsync(request, input, output, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(["translation:madlad"], result.ModelOverrides);
        Assert.Contains("Expected format: stage:alias", output.ToString());
    }

    [Fact]
    public async Task CompleteAsync_RePromptsUnsupportedExportFormat()
    {
        using var input = new StringReader(
            string.Join(
                Environment.NewLine,
                [
                    "",
                    "",
                    "",
                    "wav",
                    "mp4",
                ]));
        using var output = new StringWriter();

        var request = new DubSetupRequest(
            MediaPath: "source.mp4",
            TargetLanguage: "es",
            SourceLanguage: null,
            OutputDirectory: null,
            ModelOverrides: [],
            ExportFormat: null);

        DubSetupRequest? result = await DubSetupWizard.CompleteAsync(request, input, output, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("mp4", result.ExportFormat);
        Assert.Contains("Choose mp4 or mkv", output.ToString());
    }

    [Fact]
    public async Task CompleteAsync_SpectreAdapter_CollectsValuesFromTestConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;

        console.Input.PushTextWithEnter("source.mp4");
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);

        var request = new DubSetupRequest(
            MediaPath: null,
            TargetLanguage: null,
            SourceLanguage: null,
            OutputDirectory: null,
            ModelOverrides: [],
            ExportFormat: null);

        var adapter = new SpectreDubSetupPromptAdapter(console);
        DubSetupRequest? result = await DubSetupWizard.CompleteAsync(request, adapter, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("source.mp4", result.MediaPath);
        Assert.Equal("fr", result.TargetLanguage);
        Assert.Null(result.SourceLanguage);
        Assert.Null(result.OutputDirectory);
        Assert.Empty(result.ModelOverrides);
        Assert.Equal("mp4", result.ExportFormat);
        Assert.Contains("Trackdub dub setup", console.Output);
    }
}
