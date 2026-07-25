using System.Diagnostics;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CapturesStandardOutputAndError()
    {
        var runner = new ProcessRunner();
        ShellCommand command = CreateEchoCommand();

        ProcessResult result = await runner.RunAsync(
            command.ExecutablePath,
            command.Arguments,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("trackdub-runner-ok", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("trackdub-runner-error", result.StandardError, StringComparison.Ordinal);
        Assert.False(result.StandardOutputTruncated);
        Assert.False(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task RunAsync_WhenTimeoutElapses_KillsProcessTreeAndThrows()
    {
        var runner = new ProcessRunner();
        ShellCommand command = CreateSleepCommand();
        var stopwatch = Stopwatch.StartNew();

        ProcessRunnerTimeoutException exception = await Assert.ThrowsAsync<ProcessRunnerTimeoutException>(
            () => runner.RunAsync(
                command.ExecutablePath,
                command.Arguments,
                TestContext.Current.CancellationToken,
                new ProcessRunOptions(Timeout: TimeSpan.FromMilliseconds(250))));

        stopwatch.Stop();
        Assert.Equal(TimeSpan.FromMilliseconds(250), exception.Timeout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Timeout took {stopwatch.Elapsed}.");
    }

    private static ShellCommand CreateEchoCommand() =>
        OperatingSystem.IsWindows()
            ? new ShellCommand(
                "cmd.exe",
                ["/d", "/c", "echo trackdub-runner-ok& echo trackdub-runner-error 1>&2"])
            : new ShellCommand(
                "/bin/sh",
                ["-c", "printf 'trackdub-runner-ok'; printf 'trackdub-runner-error' 1>&2"]);

    private static ShellCommand CreateSleepCommand() =>
        OperatingSystem.IsWindows()
            ? new ShellCommand(
                "cmd.exe",
                ["/d", "/c", "ping -n 6 127.0.0.1 > nul"])
            : new ShellCommand(
                "/bin/sh",
                ["-c", "sleep 5"]);

    private sealed record ShellCommand(string ExecutablePath, IReadOnlyList<string> Arguments);
}
