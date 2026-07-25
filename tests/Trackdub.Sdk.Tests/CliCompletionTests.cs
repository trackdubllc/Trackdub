using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Parsing;

using Trackdub.Cli;

namespace Trackdub.Sdk.Tests;

[Collection(nameof(CliStdoutCaptureCollection))]
public sealed class CliCompletionTests
{
    private readonly RootCommand _rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);

    [Fact]
    public void GetCompletions_SuggestsTopLevelCommands()
    {
        ParseResult doctorParseResult = _rootCommand.Parse("doc");
        IEnumerable<CompletionItem> doctorCompletions = doctorParseResult.GetCompletions(3);

        Assert.Contains(doctorCompletions, item => string.Equals(item.Label, "doctor", StringComparison.OrdinalIgnoreCase));

        ParseResult configParseResult = _rootCommand.Parse("con");
        IEnumerable<CompletionItem> configCompletions = configParseResult.GetCompletions(3);

        Assert.Contains(configCompletions, item => string.Equals(item.Label, "config", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCompletions_SuggestsStageNames()
    {
        ParseResult parseResult = _rootCommand.Parse("run stage --stage ");
        IEnumerable<CompletionItem> completions = parseResult.GetCompletions("run stage --stage ".Length);

        Assert.Contains(completions, item => string.Equals(item.Label, "vad", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(completions, item => string.Equals(item.Label, "asr", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Normalize_StripsExecutablePrefixAndAdjustsCursor()
    {
        (string line, int position) = CliCompletionLineNormalizer.Normalize(
            "trackdub run stage --stage ",
            "trackdub run stage --stage ".Length,
            "trackdub");

        Assert.Equal("run stage --stage ", line);
        Assert.Equal("run stage --stage ".Length, position);
    }

    [Fact]
    public async Task CompleteCommand_EmitsCandidatesForPartialTopLevelCommand()
    {
        ParseResult parseResult = _rootCommand.Parse([
            "complete",
            "--position",
            "12",
            "--line",
            "trackdub doc",
        ]);

        using StringWriter stdout = CaptureStdout(out TextWriter? originalStdout);
        int exitCode = await parseResult.InvokeAsync();
        Console.SetOut(originalStdout);

        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.Contains("doctor", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompletionCommand_BashScript_ReferencesCompleteSubcommand()
    {
        ParseResult parseResult = _rootCommand.Parse(["completion", "bash"]);

        using StringWriter stdout = CaptureStdout(out TextWriter? originalStdout);
        int exitCode = await parseResult.InvokeAsync();
        Console.SetOut(originalStdout);

        Assert.Equal(Program.ExitSuccess, exitCode);
        string script = stdout.ToString();
        Assert.Contains("complete --position", script, StringComparison.Ordinal);
        Assert.Contains("complete -F _trackdub_bash_complete", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("zsh")]
    public async Task CompletionCommand_SupportedShells_ReturnSuccess(string shell)
    {
        ParseResult parseResult = _rootCommand.Parse(["completion", shell]);

        using StringWriter stdout = CaptureStdout(out TextWriter? originalStdout);
        int exitCode = await parseResult.InvokeAsync();
        Console.SetOut(originalStdout);

        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.NotEmpty(stdout.ToString());
    }

    [Fact]
    public async Task CompletionCommand_UnknownShell_ReturnsArgumentError()
    {
        ParseResult parseResult = _rootCommand.Parse(["completion", "fish"]);

        int exitCode = await parseResult.InvokeAsync();
        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    private static StringWriter CaptureStdout(out TextWriter originalStdout)
    {
        originalStdout = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        return writer;
    }
}
