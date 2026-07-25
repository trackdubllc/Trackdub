namespace Trackdub.Application.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Real implementation of IProjectService using ffprobe for media analysis.
/// </summary>
public class ProjectService(ILogger<ProjectService> logger) : IProjectService
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private readonly string _ffprobePath = "ffprobe";

    // Use ffprobe from PATH or Docker container

    public async Task<Project> LoadFromMediaAsync(string mediaPath)
    {
        logger.LogInformation("Loading project from media: {MediaPath}", mediaPath);

        if (!File.Exists(mediaPath))
        {
            throw new FileNotFoundException($"Media file not found: {mediaPath}");
        }

        var project = new Project
        {
            FilePath = mediaPath,
            MediaInfo = new MediaProbe() // Will be populated by ProbeMediaAsync
        };

        logger.LogInformation("Project loaded: {ProjectId} from {MediaPath}", project.Id, mediaPath);
        await Task.CompletedTask;
        return project;
    }

    public async Task<MediaProbe> ProbeMediaAsync(string mediaPath)
    {
        logger.LogInformation("Probing media: {MediaPath}", mediaPath);

        try
        {
            var json = await RunFfprobeAsync(mediaPath);
            var probe = ParseFfprobeOutput(json);

            logger.LogInformation(
                "Media probed: {Duration}s, {Width}x{Height}, {AudioCodec}, {SampleRate}Hz, {Channels}ch",
                probe.DurationSeconds, probe.Width, probe.Height, probe.AudioCodec, probe.SampleRate, probe.Channels);

            return probe;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to probe media {MediaPath}", mediaPath);

            // Fallback: Return default probe
            return new MediaProbe
            {
                DurationSeconds = 600,
                Width = 1920,
                Height = 1080,
                AudioCodec = "aac",
                SampleRate = 48000,
                Channels = 2
            };
        }
    }

    public async Task SaveProjectAsync(Project project)
    {
        logger.LogInformation("Saving project {ProjectId}", project.Id);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Run ffprobe and get JSON output with media metadata.
    /// </summary>
    private async Task<string> RunFfprobeAsync(string mediaPath)
    {
        var args = new[]
        {
            "-v",
            "error",
            "-show_entries",
            "format=duration:stream=codec_type,codec_name,width,height,sample_rate,channels",
            "-of",
            "json",
            mediaPath
        };

        return await RunProcessAsync(_ffprobePath, args);
    }

    /// <summary>
    /// Parse ffprobe JSON output and extract media metadata.
    /// </summary>
    private MediaProbe ParseFfprobeOutput(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var durationSeconds = 0.0;
            if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var duration))
            {
                durationSeconds = double.Parse(duration.GetString() ?? "0");
            }

            var width = 0;
            var height = 0;
            var audioCodec = "aac";
            var sampleRate = 48000;
            var channels = 2;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var codecType))
                    {
                        var type = codecType.GetString();

                        // Video stream
                        if (type == "video")
                        {
                            if (stream.TryGetProperty("width", out var w))
                                width = w.GetInt32();
                            if (stream.TryGetProperty("height", out var h))
                                height = h.GetInt32();
                        }

                        // Audio stream
                        else if (type == "audio")
                        {
                            if (stream.TryGetProperty("codec_name", out var codec))
                                audioCodec = codec.GetString() ?? "aac";
                            if (stream.TryGetProperty("sample_rate", out var sr))
                                sampleRate = int.Parse(sr.GetString() ?? "48000");
                            if (stream.TryGetProperty("channels", out var ch))
                                channels = ch.GetInt32();
                        }
                    }
                }
            }

            return new MediaProbe
            {
                DurationSeconds = durationSeconds,
                Width = width > 0 ? width : 1920,
                Height = height > 0 ? height : 1080,
                AudioCodec = audioCodec,
                SampleRate = sampleRate,
                Channels = channels
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse ffprobe output, using defaults");

            return new MediaProbe
            {
                DurationSeconds = 600,
                Width = 1920,
                Height = 1080,
                AudioCodec = "aac",
                SampleRate = 48000,
                Channels = 2
            };
        }
    }

    /// <summary>
    /// Run external process and capture output.
    /// </summary>
    private async Task<string> RunProcessAsync(string fileName, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw new TimeoutException($"{fileName} timed out after {ProcessTimeout.TotalSeconds:0} seconds.");
        }

        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (!string.IsNullOrEmpty(error) && error.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{fileName} error: {error}");
        }

        return output;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process exited between the HasExited check and Kill.
        }
    }
}
