using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli;

namespace Trackdub.Sdk.Tests;

public sealed class CliBatchInputValidationTests
{
    [Theory]
    [InlineData("dub", "--media", "media.mp4", "--input-dir", "input")]
    [InlineData("run", "pipeline", "--media", "media.mp4", "--input-dir", "input")]
    public async Task Cli_WhenMediaAndInputDirSpecified_ReturnsValidationError(params string[] args)
    {
        (int exitCode, string stderr) = await InvokeCliAsync(args);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        Assert.Contains("mutually exclusive", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--input-dir", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dub", "--media", "media.mp4", "--input-glob", "*.wav")]
    [InlineData("run", "pipeline", "--media", "media.mp4", "--input-glob", "*.wav")]
    public async Task Cli_WhenMediaAndInputGlobSpecified_ReturnsValidationError(params string[] args)
    {
        (int exitCode, string stderr) = await InvokeCliAsync(args);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        Assert.Contains("mutually exclusive", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--input-glob", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dub", "--input-dir", "input", "--input-glob", "*.wav")]
    [InlineData("run", "pipeline", "--input-dir", "input", "--input-glob", "*.wav")]
    public async Task Cli_WhenInputDirAndInputGlobSpecified_ReturnsValidationError(params string[] args)
    {
        (int exitCode, string stderr) = await InvokeCliAsync(args);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        Assert.Contains("mutually exclusive", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--input-dir", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stderr)> InvokeCliAsync(string[] args)
    {
        TextWriter originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);

        try
        {
            RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
            ParseResult parseResult = rootCommand.Parse(args);
            int exitCode = await parseResult.InvokeAsync().ConfigureAwait(false);
            return (exitCode, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
