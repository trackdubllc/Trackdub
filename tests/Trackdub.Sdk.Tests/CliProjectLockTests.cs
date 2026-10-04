using System.Security;

using Microsoft.Extensions.DependencyInjection;

using Trackdub.Cli;
using Trackdub.Cli.Handlers;
using Trackdub.Contracts.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Covers the CLI half of the project lock: a CLI run and a desktop run against the same
/// project must be mutually excluded, and the second must fail cleanly naming the holder.
/// </summary>
[Collection(nameof(CliStdoutCaptureCollection))]
public sealed class CliProjectLockTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), "TrackdubTests", "CliProjectLock", Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _held = [];

    public void Dispose()
    {
        foreach (IDisposable disposable in _held)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void DescribeProjectLocked_OtherProcess_NamesTheHolderPid()
    {
        int otherPid = Environment.ProcessId + 1;
        var exception = new ProjectLockedException(@"C:\projects\clip.trackdub", otherPid);

        string message = CliProjectLock.DescribeProjectLocked(exception);

        Assert.Contains($"PID {otherPid}", message, StringComparison.Ordinal);
        Assert.Contains("another Trackdub process", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeProjectLocked_SameProcess_SaysARunIsAlreadyInProgress()
    {
        var exception = new ProjectLockedException(@"C:\projects\clip.trackdub", Environment.ProcessId);

        string message = CliProjectLock.DescribeProjectLocked(exception);

        Assert.Contains("already in progress", message, StringComparison.Ordinal);
        Assert.Contains("in this app", message, StringComparison.Ordinal);
        Assert.DoesNotContain("PID", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeProjectLocked_UnknownHolder_ReportsConflictWithoutAPid()
    {
        var exception = new ProjectLockedException(@"C:\projects\clip.trackdub");

        string message = CliProjectLock.DescribeProjectLocked(exception);

        Assert.Contains("another Trackdub process", message, StringComparison.Ordinal);
        Assert.DoesNotContain("PID", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAcquire_UncontendedProject_KeepsSidecarAndAllowsReacquire()
    {
        string project = CreateProjectDirectory();

        ProjectLock? acquired = CliProjectLock.TryAcquire(project);
        Assert.NotNull(acquired);
        Assert.True(File.Exists(Path.Join(project, ".trackdub.lock")));

        acquired.Dispose();
        Assert.True(File.Exists(Path.Join(project, ".trackdub.lock")));

        ProjectLock? reacquired = CliProjectLock.TryAcquire(project);
        Assert.NotNull(reacquired);
        reacquired.Dispose();
    }

    [Fact]
    public void TryAcquire_ProjectAlreadyHeld_ReturnsNull()
    {
        string project = CreateProjectDirectory();
        _held.Add(ProjectLock.Acquire(project));

        Assert.Null(CliProjectLock.TryAcquire(project));
    }

    [Fact]
    public async Task RunStage_WhenProjectIsLocked_FailsCleanlyWithProjectLocked()
    {
        string project = CreateProjectDirectory();
        _held.Add(ProjectLock.Acquire(project));

        (int exitCode, string stderr) = await InvokeRunStageAsync(project);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
        Assert.Contains("projectLocked", stderr, StringComparison.Ordinal);
        Assert.Contains("already in progress", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPipeline_WhenProjectIsLocked_FailsCleanlyWithProjectLocked()
    {
        string project = CreateProjectDirectory();
        _held.Add(ProjectLock.Acquire(project));

        (int exitCode, string stderr) = await InvokeRunPipelineAsync(project);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
        Assert.Contains("projectLocked", stderr, StringComparison.Ordinal);
        Assert.Contains("already in progress", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAcquire_ProjectDirectoryUnreachable_ReportsRuntimeUnavailableInsteadOfThrowing()
    {
        string blockingFile = Path.Join(_root, Guid.NewGuid().ToString("N") + ".mp4");
        Directory.CreateDirectory(_root);
        File.WriteAllText(blockingFile, string.Empty);

        // A path whose parent is a regular file cannot be created, so Acquire fails on I/O
        // rather than on a lock conflict. The CLI must still exit with a structured error.
        string unreachableProject = Path.Join(blockingFile, "clip.trackdub");

        TextWriter originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);

        ProjectLock? acquired;
        try
        {
            acquired = CliProjectLock.TryAcquire(unreachableProject);
        }
        finally
        {
            Console.SetError(originalError);
        }

        string reported = stderr.ToString();

        Assert.Null(acquired);
        Assert.Contains("runtimeUnavailable", reported, StringComparison.Ordinal);
        Assert.Contains("Cannot acquire the project lock for", reported, StringComparison.Ordinal);
        Assert.Contains("clip.trackdub", reported, StringComparison.Ordinal);
        Assert.DoesNotContain("projectLocked", reported, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("security")]
    [InlineData("unsupported")]
    public void TryAcquire_DefensiveFilesystemFailures_ReportsRuntimeUnavailableInsteadOfThrowing(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "security" => new SecurityException("simulated security policy denial"),
            "unsupported" => new NotSupportedException("simulated unsupported filesystem operation"),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };

        TextWriter originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);

        ProjectLock? acquired;
        try
        {
            acquired = CliProjectLock.TryAcquire(_root, _ => throw failure);
        }
        finally
        {
            Console.SetError(originalError);
        }

        string reported = stderr.ToString();

        Assert.Null(acquired);
        Assert.Contains("runtimeUnavailable", reported, StringComparison.Ordinal);
        Assert.Contains("Cannot acquire the project lock for", reported, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProjectDirectory_WithoutExplicitOutput_DerivesTrackdubFolderBesideTheMedia()
    {
        var request = new RunPipelineHandler.RunPipelineRequest
        {
            SourceMediaPath = Path.Join("media-dir", "clip.mp4"),
            TargetLanguageCode = "fr",
        };

        string resolved = RunPipelineHandler.ResolveProjectDirectory(request);

        Assert.Equal(Path.Join("media-dir", "clip.trackdub"), resolved);
    }

    [Fact]
    public void ResolveProjectDirectory_WithExplicitOutput_UsesItUnchanged()
    {
        string explicitDirectory = Path.Join("output-dir", "my-project");
        var request = new RunPipelineHandler.RunPipelineRequest
        {
            SourceMediaPath = Path.Join("media-dir", "clip.mp4"),
            ProjectOutputDirectory = explicitDirectory,
            TargetLanguageCode = "fr",
        };

        Assert.Equal(explicitDirectory, RunPipelineHandler.ResolveProjectDirectory(request));
    }

    private static async Task<(int ExitCode, string Stderr)> InvokeRunStageAsync(string project)
    {
        using TrackdubSessionFactory factory =
            new TrackdubSessionFactory(new ServiceCollection().BuildServiceProvider());

        TextWriter originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);

        try
        {
            int exitCode = await RunStageHandler.ExecuteAsync(
                factory,
                project,
                StageNames.Vad,
                modelAlias: null,
                progress: null,
                TextWriter.Null,
                CancellationToken.None).ConfigureAwait(false);

            return (exitCode, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static async Task<(int ExitCode, string Stderr)> InvokeRunPipelineAsync(string project)
    {
        using TrackdubSessionFactory factory =
            new TrackdubSessionFactory(new ServiceCollection().BuildServiceProvider());

        var request = new RunPipelineHandler.RunPipelineRequest
        {
            SourceMediaPath = Path.Join(Path.GetDirectoryName(project)!, "clip.mp4"),
            ProjectOutputDirectory = project,
            TargetLanguageCode = "fr",
        };

        TextWriter originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);

        try
        {
            int exitCode = await RunPipelineHandler.ExecuteAsync(
                factory,
                request,
                progress: null,
                TextWriter.Null,
                CancellationToken.None).ConfigureAwait(false);

            return (exitCode, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private string CreateProjectDirectory()
    {
        string project = Path.Join(_root, Guid.NewGuid().ToString("N") + ".trackdub");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Join(project, ProjectArtifactPaths.DatabaseFileName), string.Empty);
        return project;
    }
}
