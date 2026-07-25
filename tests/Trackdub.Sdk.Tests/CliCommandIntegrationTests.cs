using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Integration tests for CLI commands invoked programmatically via System.CommandLine.
/// These tests verify exit codes and structured error reporting for various scenarios.
/// </summary>
/// <remarks>
/// Validates: Requirements 2.6, 7.5, 8.2, 9.5, 11.1, 11.2
/// </remarks>
public sealed class CliCommandIntegrationTests : IDisposable
{
    private readonly string _emptyModelDirectory = Path.Combine(
        Path.GetTempPath(),
        "TrackdubTests",
        Guid.NewGuid().ToString("N"),
        "models");

    public CliCommandIntegrationTests()
    {
        Directory.CreateDirectory(_emptyModelDirectory);
    }

    /// <summary>
    /// Invokes the CLI root command with the given arguments and returns the exit code.
    /// Uses <see cref="Program.BuildRootCommand(Func{bool}?)"/> which is accessible via InternalsVisibleTo.
    /// </summary>
    private Task<int> InvokeCliAsync(params string[] args) =>
        InvokeCliWithModelDirectory(modelDirectory: null, args);

    private async Task<int> InvokeCliWithModelDirectory(string? modelDirectory, string[] args)
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        string[] effectiveArgs = modelDirectory is null
            ? args
            : ["--model-directory", modelDirectory, .. args];
        ParseResult parseResult = rootCommand.Parse(effectiveArgs);
        return await parseResult.InvokeAsync();
    }

    [Fact]
    public async Task DubCommand_MissingMedia_ReturnsExitCode1()
    {
        // Arrange: invoke dub with a media file that does not exist
        string nonExistentMedia = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.mp4");

        // Act
        int exitCode = await InvokeCliAsync("dub", "--media", nonExistentMedia, "--target-language", "es");

        // Assert: exit code 1 indicates argument/validation error (media not found)
        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public async Task DubCommand_MissingRequiredArgs_ReturnsExitCode1()
    {
        // Arrange & Act: invoke dub with no arguments at all
        int exitCode = await InvokeCliAsync("dub");

        // Assert: exit code 1 for missing required arguments
        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public async Task RunStageCommand_MissingProject_ReturnsExitCode2()
    {
        // Arrange: invoke run-stage with a project directory that does not exist
        string nonExistentProject = Path.Combine(Path.GetTempPath(), $"nonexistent-project-{Guid.NewGuid():N}");

        // Act
        int exitCode = await InvokeCliAsync("run-stage", "--project", nonExistentProject, "--stage", "vad");

        // Assert: exit code 2 indicates pipeline execution failure (project not found)
        Assert.Equal(Program.ExitPipelineFailure, exitCode);
    }

    [Fact]
    public async Task CheckCommand_WhenModelsMissing_ReturnsExitCode2()
    {
        int exitCode = await InvokeCliWithModelDirectory(_emptyModelDirectory, ["check"]);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
    }

    [Fact]
    public async Task CheckCommand_InvalidProject_ReturnsExitCode1()
    {
        string missingProject = Path.Combine(Path.GetTempPath(), $"missing-project-{Guid.NewGuid():N}");
        int exitCode = await InvokeCliAsync("check", "--project", missingProject);

        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public async Task ModelsStatusCommand_WithEmptyModelDirectory_ReturnsPipelineFailure()
    {
        int exitCode = await InvokeCliWithModelDirectory(_emptyModelDirectory, ["models", "status"]);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
    }

    [Fact]
    public async Task ModelsDownloadCommand_UnknownModel_ReturnsPipelineFailure()
    {
        int exitCode = await InvokeCliWithModelDirectory(_emptyModelDirectory, ["models", "download", "definitely-not-a-trackdub-model-id"]);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
    }

    [Fact]
    public async Task DoctorCommand_WithEmptyModelDirectory_ReturnsPipelineFailure()
    {
        int exitCode = await InvokeCliWithModelDirectory(_emptyModelDirectory, ["doctor"]);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
    }

    [Fact]
    public async Task DoctorCommand_CreatesMissingModelDirectoryBeforeRunning()
    {
        string missingModelDirectory = Path.Combine(
            Path.GetTempPath(),
            "TrackdubTests",
            Guid.NewGuid().ToString("N"),
            "models");

        try
        {
            int exitCode = await InvokeCliWithModelDirectory(missingModelDirectory, ["doctor"]);
            Assert.Equal(Program.ExitPipelineFailure, exitCode);
            Assert.True(Directory.Exists(missingModelDirectory));
        }
        finally
        {
            try
            {
                if (Directory.Exists(missingModelDirectory))
                {
                    string? parentDirectory = Path.GetDirectoryName(missingModelDirectory);
                    if (!string.IsNullOrEmpty(parentDirectory) && Directory.Exists(parentDirectory))
                    {
                        Directory.Delete(parentDirectory, recursive: true);
                    }
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task ModelsBundleNeeded_WithEmptyModelDirectory_ReturnsPipelineFailure()
    {
        int exitCode = await InvokeCliWithModelDirectory(_emptyModelDirectory, ["models", "bundle-needed"]);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
    }

    [Fact]
    public async Task ProjectInfo_InvalidProject_ReturnsExitCode1()
    {
        string missingProject = Path.Combine(Path.GetTempPath(), $"missing-project-{Guid.NewGuid():N}");
        int exitCode = await InvokeCliAsync("project", "info", "--project", missingProject);

        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public async Task RunStageAlias_InvalidProject_ReturnsExitCode2()
    {
        string nonExistentProject = Path.Combine(Path.GetTempPath(), $"nonexistent-project-{Guid.NewGuid():N}");
        int exitCode = await InvokeCliAsync("run", "stage", "--project", nonExistentProject, "--stage", "vad");

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
    }

    [Fact]
    public async Task RunPipeline_MissingRequiredArgs_ReturnsExitCode1()
    {
        int exitCode = await InvokeCliAsync("run", "pipeline");

        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public async Task RunPipeline_UnknownOnlyStage_ReturnsExitCode1()
    {
        int exitCode = await InvokeCliAsync(
            "run",
            "pipeline",
            "--media",
            Path.Combine(Path.GetTempPath(), "missing-media.mp4"),
            "--target-language",
            "es",
            "--only",
            "transcription");

        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public void NormalizeHelpExecutableName_RewritesRootUsageCommand()
    {
        const string helpText = """
            Description:
              Trackdub CLI - headless dubbing pipeline

            Usage:
              Trackdub.Cli [command] [options]
            """;

        string normalized = Program.NormalizeHelpExecutableName(helpText);

        Assert.Contains("Trackdub CLI - headless dubbing pipeline", normalized, StringComparison.Ordinal);
        Assert.Contains("Usage:\n  trackdub [command] [options]", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:\n  Trackdub.Cli", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsDownload_AllMissing_ParsesWithoutModelId()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse(["models", "download", "--all-missing"]);

        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void TrackdubProjectPaths_MissingDirectory_ReturnsFalse()
    {
        string missingProject = Path.Combine(Path.GetTempPath(), $"missing-project-{Guid.NewGuid():N}");

        Assert.False(TrackdubProjectPaths.ContainsDatabase(missingProject));
    }

    [Fact]
    public async Task RunStageCommand_InvalidStageName_ReturnsNonZeroExitCode()
    {
        // Arrange: invoke run-stage with an invalid stage name that won't pass validation
        string tempProject = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid():N}");

        // Act: "invalid-stage" is not in the accepted stage names list
        int exitCode = await InvokeCliAsync("run-stage", "--project", tempProject, "--stage", "invalid-stage");

        // Assert: non-zero exit code for parse/validation error
        Assert.NotEqual(Program.ExitSuccess, exitCode);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_emptyModelDirectory))
            {
                string? parentDirectory = Path.GetDirectoryName(_emptyModelDirectory);
                if (!string.IsNullOrEmpty(parentDirectory) && Directory.Exists(parentDirectory))
                {
                    Directory.Delete(parentDirectory, recursive: true);
                }
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
