using System.Diagnostics;
using System.Text;
using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Licensing;

internal sealed class HuggingFaceCliDownloader
{
    internal const int ConsecutiveFailuresBeforeStickyDisable = 2;
    private static readonly TimeSpan StaleTempTtl = TimeSpan.FromHours(6);

    private readonly HuggingFaceDownloadOptions options;
    private readonly IApplicationLogger logger;
    private readonly string? hfExecutable;
    private readonly object stateLock = new();
    private int consecutiveFailures;
    private bool stickyDisabled;
    private bool stickyDisableLogged;
    private int staleTempsCleaned;

    public HuggingFaceCliDownloader(HuggingFaceDownloadOptions options, IApplicationLogger logger)
    {
        this.options = options;
        this.logger = logger;
        hfExecutable = options.CliPreference == HuggingFaceCliPreference.Never
            ? null
            : HuggingFaceCliLocator.TryResolveExecutable();
    }

    public bool IsAvailable =>
        options.CliPreference != HuggingFaceCliPreference.Never && hfExecutable is not null;

    public bool IsStickyDisabled
    {
        get
        {
            lock (stateLock)
            {
                return stickyDisabled;
            }
        }
    }

    public bool ShouldAttempt
    {
        get
        {
            lock (stateLock)
            {
                return !stickyDisabled &&
                    options.CliPreference switch
                    {
                        HuggingFaceCliPreference.Required => IsAvailable,
                        HuggingFaceCliPreference.Auto => IsAvailable,
                        _ => false,
                    };
            }
        }
    }

    public async Task<bool> TryDownloadAsync(
        string modelId,
        string fileName,
        string destinationPath,
        string revision,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        CleanupStaleTempDirectoriesOnce();

        if (!ShouldAttempt || hfExecutable is null)
        {
            if (options.CliPreference == HuggingFaceCliPreference.Required && !IsStickyDisabled)
            {
                logger.LogError(
                    "TRACKDUB_HF_USE_CLI=require but the Hugging Face CLI ('hf') was not found on PATH. Install it with: pip install -U huggingface_hub[cli] hf_transfer");
            }

            return false;
        }

        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.HfCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var stderrLines = new List<string>();
        try
        {
            logger.LogInformation(
                $"Downloading via Hugging Face CLI ({hfExecutable}) with hf_transfer={(options.EnableCliTransfer ? "enabled" : "disabled")}, disable_xet={(options.DisableXet ? "enabled" : "disabled")}.");

            var arguments = new List<string>
            {
                "download",
                modelId,
                "--revision",
                revision,
                "--include",
                fileName,
                "--local-dir",
                tempDirectory,
                "--max-workers",
                options.MaxParallelConnections.ToString(),
            };

            int exitCode = await RunProcessAsync(hfExecutable, arguments, stderrLines, cancellationToken)
                .ConfigureAwait(false);
            if (exitCode != 0)
            {
                logger.LogWarning($"Hugging Face CLI download exited with code {exitCode} for {modelId}/{fileName}.");
                RecordFailure(stderrLines);
                return false;
            }

            string downloadedPath = Path.Combine(tempDirectory, fileName);
            if (!File.Exists(downloadedPath))
            {
                downloadedPath = Directory
                    .EnumerateFiles(tempDirectory, Path.GetFileName(fileName), SearchOption.AllDirectories)
                    .FirstOrDefault() ?? downloadedPath;
            }

            if (!File.Exists(downloadedPath))
            {
                logger.LogWarning($"Hugging Face CLI reported success but '{fileName}' was not found under {tempDirectory}.");
                RecordFailure(stderrLines);
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(downloadedPath, destinationPath, overwrite: true);

            long bytes = new FileInfo(destinationPath).Length;
            progress?.Report(new DownloadProgress(
                bytes,
                bytes,
                100,
                $"Downloaded {bytes} bytes via Hugging Face CLI",
                null,
                TimeSpan.Zero));

            lock (stateLock)
            {
                consecutiveFailures = 0;
            }

            logger.LogInformation($"Hugging Face CLI download completed: {destinationPath}");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Hugging Face CLI download failed for {modelId}/{fileName}.", ex);
            RecordFailure(stderrLines);
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private void RecordFailure(IReadOnlyList<string> stderrLines)
    {
        lock (stateLock)
        {
            consecutiveFailures++;
            bool encodingFailure = stderrLines.Any(IsEncodingFailureLine);
            if (encodingFailure || consecutiveFailures >= ConsecutiveFailuresBeforeStickyDisable)
            {
                stickyDisabled = true;
                if (!stickyDisableLogged)
                {
                    stickyDisableLogged = true;
                    string reason = encodingFailure
                        ? "encoding/charmap failure"
                        : $"{consecutiveFailures} consecutive failures";
                    string nextStep = options.CliPreference == HuggingFaceCliPreference.Required
                        ? "CLI is required; HTTP fallback will be refused."
                        : "Falling back to HTTP downloads.";
                    logger.LogWarning(
                        $"Hugging Face CLI disabled for this session after {reason}. {nextStep}");
                }
            }
        }
    }

    /// <summary>Test seam for sticky-disable behavior without spawning hf.</summary>
    internal void RecordFailureForTests(params string[] stderrLines) =>
        RecordFailure(stderrLines);

    internal static bool IsEncodingFailureLine(string line) =>
        line.Contains("charmap", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("codec can't encode", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("codec can't decode", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("UnicodeEncodeError", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("UnicodeDecodeError", StringComparison.OrdinalIgnoreCase);

    private async Task<int> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        List<string> stderrLines,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (KeyValuePair<string, string> variable in options.BuildHubCliEnvironmentVariables())
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task stdoutTask = PumpAsync(process.StandardOutput, collect: null, cancellationToken);
        Task stderrTask = PumpAsync(process.StandardError, stderrLines, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await DrainPumpsIgnoringCancelNoiseAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            cancellationToken.IsCancellationRequested &&
            ex is ObjectDisposedException or IOException)
        {
            throw new OperationCanceledException("Hugging Face CLI download was cancelled.", ex, cancellationToken);
        }

        return process.ExitCode;
    }

    private static async Task DrainPumpsIgnoringCancelNoiseAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or ObjectDisposedException or IOException ||
            ex is AggregateException aggregate &&
            aggregate.InnerExceptions.All(static inner =>
                inner is OperationCanceledException or ObjectDisposedException or IOException))
        {
            // Expected when cancel kills the process and redirected pipes close.
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Process already exited or kill unsupported on platform.
        }
    }

    private async Task PumpAsync(
        StreamReader reader,
        List<string>? collect,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                cancellationToken.IsCancellationRequested &&
                ex is ObjectDisposedException or IOException)
            {
                throw new OperationCanceledException("Hugging Face CLI output pump cancelled.", ex, cancellationToken);
            }

            if (line is null)
            {
                break;
            }

            collect?.Add(line);

            if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning($"hf: {line}");
            }
        }
    }

    private void CleanupStaleTempDirectoriesOnce()
    {
        if (Interlocked.Exchange(ref staleTempsCleaned, 1) != 0)
        {
            return;
        }

        CleanupStaleTempDirectories(logger, StaleTempTtl);
    }

    internal static void CleanupStaleTempDirectories(
        IApplicationLogger logger,
        TimeSpan ttl,
        string? rootDirectory = null)
    {
        string root = rootDirectory ?? Path.Combine(Path.GetTempPath(), "Trackdub.HfCli");
        if (!Directory.Exists(root))
        {
            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - ttl;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    DateTimeOffset stamp = info.LastWriteTimeUtc > info.CreationTimeUtc
                        ? info.LastWriteTimeUtc
                        : info.CreationTimeUtc;
                    if (stamp > cutoff)
                    {
                        continue;
                    }

                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // Best-effort orphan cleanup only.
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Failed to clean stale Hugging Face CLI temp directories under '{root}'.", ex);
        }
    }
}

internal static class HuggingFaceCliLocator
{
    public static string? TryResolveExecutable()
    {
        foreach (string candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory, OperatingSystem.IsWindows() ? "hf.exe" : "hf");
            if (OperatingSystem.IsWindows())
            {
                yield return Path.Combine(directory, "hf.cmd");
                yield return Path.Combine(directory, "hf");
            }
        }
    }
}
