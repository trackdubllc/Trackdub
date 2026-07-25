using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli;
using Trackdub.Cli.Commands;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Unit tests for <see cref="RunStageCommand"/> argument parsing and validation.
/// Validates: Requirements 8.1, 8.2, 8.3
/// </summary>
public sealed class RunStageCommandTests
{
    private readonly RootCommand _rootCommand = Program.BuildRootCommand();

    [Fact]
    public void RunStageCommand_RequiresProjectArgument()
    {
        var parseResult = _rootCommand.Parse("run-stage --stage vad");
        Assert.NotEmpty(parseResult.Errors);
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("--project"));
    }

    [Fact]
    public void RunStageCommand_RequiresStageArgument()
    {
        var parseResult = _rootCommand.Parse("run-stage --project ./some-dir");
        Assert.NotEmpty(parseResult.Errors);
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("--stage"));
    }

    [Theory]
    [InlineData("invalid-stage")]
    [InlineData("transcription")]
    [InlineData("VAD")]
    [InlineData("ASR")]
    [InlineData("")]
    public void RunStageCommand_StageOption_OnlyAcceptsCanonicalNames(string invalidStageName)
    {
        var parseResult = _rootCommand.Parse($"run-stage --project ./some-dir --stage {invalidStageName}");
        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void RunStageCommand_OldCommercialSafeFlag_IsRejected()
    {
        var parseResult = _rootCommand.Parse("run-stage --project ./some-dir --stage vad --commercial-safe false");
        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void GlobalModelDirectoryOption_IsReadableFromSubcommandParseResult()
    {
        string modelDirectory = Path.Combine(Path.GetTempPath(), "trackdub-cli-global-model-dir-test");
        ParseResult parseResult = _rootCommand.Parse(
            ["--model-directory", modelDirectory, "check"]);
        string? parsedModelDirectory = CliParseHelpers.GetGlobalOptionValue<string?>(parseResult, "model-directory");

        Assert.Equal(modelDirectory, parsedModelDirectory);
    }

    [Theory]
    [InlineData(StageNames.Separation)]
    [InlineData(StageNames.Vad)]
    [InlineData(StageNames.Asr)]
    [InlineData(StageNames.Diarization)]
    [InlineData(StageNames.Translation)]
    [InlineData(StageNames.Tts)]
    [InlineData(StageNames.Export)]
    public void RunStageCommand_ValidStageNames_Accepted(string validStageName)
    {
        var parseResult = _rootCommand.Parse($"run-stage --project ./some-dir --stage {validStageName}");
        Assert.Empty(parseResult.Errors);
    }
}
