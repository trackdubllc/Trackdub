using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Unit tests for the <c>dub</c> command argument parsing behavior.
/// Uses <see cref="RootCommand.Parse"/> to inspect <see cref="ParseResult"/>
/// without invoking the handler.
/// </summary>
/// <remarks>Validates: Requirements 7.1, 7.2, 7.3</remarks>
public sealed class DubCommandArgumentParsingTests
{
    private readonly RootCommand _rootCommand = Program.BuildRootCommand();

    private Command GetDubCommand() =>
        (Command)_rootCommand.Subcommands.First(c => c.Name == "dub");

    private static Option<T> FindOption<T>(Command command, string nameWithDashes) =>
        (Option<T>)command.Options.First(o => o.Name == nameWithDashes);

    [Fact]
    public void DubCommand_MissingMedia_IsHandledBySetupWizard()
    {
        var parseResult = _rootCommand.Parse("dub --target-language es");
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void DubCommand_MissingTargetLanguage_IsHandledBySetupWizard()
    {
        var parseResult = _rootCommand.Parse("dub --media test.mp4");
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void DubCommand_OutputOption_IsOptional()
    {
        var parseResult = _rootCommand.Parse("dub --media test.mp4 --target-language es");
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void DubCommand_OldCommercialSafeFlag_IsRejected()
    {
        var parseResult = _rootCommand.Parse("dub --media test.mp4 --target-language es --commercial-safe false");
        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void DubCommand_ModelOption_ParsesMultipleValues()
    {
        var parseResult = _rootCommand.Parse("dub --media test.mp4 --target-language es --model asr:large-v3 --model tts:xtts-v2");

        var modelOption = FindOption<string[]>(GetDubCommand(), "--model");
        string[] models = parseResult.GetValue(modelOption) ?? [];

        Assert.Equal(2, models.Length);
        Assert.Contains("asr:large-v3", models);
        Assert.Contains("tts:xtts-v2", models);
    }

    [Fact]
    public void DubCommand_ModelOption_EmptyByDefault()
    {
        var parseResult = _rootCommand.Parse("dub --media test.mp4 --target-language es");

        var modelOption = FindOption<string[]>(GetDubCommand(), "--model");
        string[] models = parseResult.GetValue(modelOption) ?? [];

        Assert.Empty(models);
    }

    [Fact]
    public void DubCommand_ExportFormat_AcceptsMkv()
    {
        var parseResult = _rootCommand.Parse("dub --media test.mp4 --target-language es --export-format mkv");
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void DubCommand_ExportFormat_RejectsUnsupportedContainer()
    {
        var parseResult = _rootCommand.Parse("dub --media test.mp4 --target-language es --export-format wav");

        Assert.NotEmpty(parseResult.Errors);
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("mkv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DubCommand_AllRequiredArgs_ProducesNoErrors()
    {
        var parseResult = _rootCommand.Parse("dub --media test.mp4 --target-language es");
        Assert.Empty(parseResult.Errors);
    }
}
