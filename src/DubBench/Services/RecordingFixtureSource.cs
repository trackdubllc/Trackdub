using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DubBench.Services;

/// <summary>
/// Platform-adaptive recording fixture source.
/// On Windows, uses FFmpeg to capture from default audio/video devices.
/// On other platforms, always reports unavailable.
/// </summary>
public sealed class RecordingFixtureSource : IRecordingFixtureSource
{
    private bool? _availability;

    public bool IsAvailable => _availability ?? false;

    public Task<bool> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
    {
#if WINDOWS
        return ProbeWindowsAvailabilityAsync(cancellationToken);
#else
        _availability = false;
        return Task.FromResult(false);
#endif
    }

    public async Task<RecordingResult?> CaptureAsync(
        string outputDir,
        TimeSpan? maxDuration = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable && !await ProbeAvailabilityAsync(cancellationToken).ConfigureAwait(false))
            return null;

#if WINDOWS
        return await CaptureWindowsAsync(outputDir, maxDuration ?? TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
#else
        return null;
#endif
    }

#if WINDOWS
    private async Task<bool> ProbeWindowsAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-version");

            process.Start();
            await DrainAndWaitAsync(process, cancellationToken).ConfigureAwait(false);
            _availability = process.ExitCode == 0;
            return _availability.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _availability = false;
            return false;
        }
    }

    private async Task<RecordingResult?> CaptureWindowsAsync(
        string outputDir,
        TimeSpan maxDuration,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"recording_{DateTime.UtcNow:yyyyMMdd-HHmmss}.mp4");

        var durationSec = (int)maxDuration.TotalSeconds;

        // Enumerate available dshow audio devices at runtime instead of using a hardcoded device name.
        string? audioDevice = await TryEnumerateDshowAudioDeviceAsync(cancellationToken).ConfigureAwait(false);
        if (audioDevice is null)
            return null;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("dshow");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add($"audio={audioDevice}");
            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add(durationSec.ToString());
            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add(outputPath);

            process.Start();
            await DrainAndWaitAsync(process, cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
                return null;

            return new RecordingResult(
                outputPath,
                maxDuration,
                44100, // standard sample rate
                1);    // mono
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the first available dshow audio device name by running
    /// <c>ffmpeg -list_devices true -f dshow -i dummy</c> and parsing stderr.
    /// Returns null if FFmpeg is unavailable or no audio device is found.
    /// </summary>
    private static async Task<string?> TryEnumerateDshowAudioDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-list_devices");
            process.StartInfo.ArgumentList.Add("true");
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("dshow");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add("dummy");

            process.Start();
            string stderr = string.Empty;
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdoutTask, stderrTask)
                    .ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            // FFmpeg outputs audio devices after the "DirectShow audio devices" line.
            // Each device line looks like: [dshow @ 0x...] "Device Name" (audio)
            bool inAudioSection = false;
            foreach (string line in stderr.Split('\n'))
            {
                if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
                {
                    inAudioSection = true;
                    continue;
                }

                if (inAudioSection)
                {
                    // Stop if we hit the next section header
                    if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
                        break;

                    Match m = Regex.Match(line, @"""([^""]+)""");
                    if (m.Success)
                        return m.Groups[1].Value;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drains stdout/stderr concurrently while waiting for exit.
    /// Kills the process on cancellation to prevent orphaned children.
    /// </summary>
    private static async Task DrainAndWaitAsync(Process process, CancellationToken cancellationToken)
    {
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(cancellationToken),
                stdoutTask,
                stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
#endif
}
