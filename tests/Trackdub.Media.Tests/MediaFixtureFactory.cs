using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

internal static class MediaFixtureFactory
{
    public static string? FfmpegSkipReason =>
        ResolveExecutable("TRACKDUB_FFMPEG_PATH", ["ffmpeg.exe", "ffmpeg"]) is null
            ? "ffmpeg is not available on PATH or through TRACKDUB_FFMPEG_PATH."
            : null;

    public static string? FfprobeSkipReason =>
        ResolveExecutable("TRACKDUB_FFPROBE_PATH", ["ffprobe.exe", "ffprobe"]) is null
            ? "ffprobe is not available on PATH or through TRACKDUB_FFPROBE_PATH."
            : null;

    public static async Task<string> CreateSampleVideoAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directoryPath);
        string outputPath = Path.Combine(directoryPath, "sample-input.mp4");
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable("TRACKDUB_FFMPEG_PATH", ["ffmpeg.exe", "ffmpeg"])
                ?? throw new InvalidOperationException("ffmpeg is not available on PATH or through TRACKDUB_FFMPEG_PATH."),
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in new[]
                 {
                     "-y",
                     "-hide_banner",
                     "-loglevel", "error",
                     "-f", "lavfi",
                     "-i", "sine=frequency=880:duration=1.25",
                     "-f", "lavfi",
                     "-i", "color=color=blue:size=64x64:duration=1.25:rate=24",
                     "-shortest",
                     "-c:v", "libx264",
                     "-pix_fmt", "yuv420p",
                     "-c:a", "aac",
                     outputPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'. Ensure ffmpeg is installed and available on PATH.");

        using (process)
        {
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                Assert.True(process.ExitCode == 0, $"ffmpeg failed to create test media: {error}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    Debug.WriteLine($"Failed to kill cancelled fixture process: {ex}");
                }

                try
                {
                    // CancellationToken.None is intentional: the outer token is already cancelled;
                    // we need an uncancelled token to drain the process before re-throwing.
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }

                try
                {
                    await errorTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }

                throw;
            }
        }

        return outputPath;
    }

    private static string? ResolveExecutable(string environmentVariable, IReadOnlyList<string> fallbackNames)
    {
        return FfmpegToolResolver.TryResolveExecutableForCurrentProcess(null, environmentVariable, fallbackNames, allowAutoDownload: false);
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath: sourceFilePath, sourceLineNumber: sourceLineNumber)
    {
        Skip = MediaFixtureFactory.FfmpegSkipReason;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class RequiresFfmpegAndFfprobeFactAttribute : FactAttribute
{
    public RequiresFfmpegAndFfprobeFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath: sourceFilePath, sourceLineNumber: sourceLineNumber)
    {
        string? ffmpeg = MediaFixtureFactory.FfmpegSkipReason;
        string? ffprobe = MediaFixtureFactory.FfprobeSkipReason;
        Skip = (ffmpeg, ffprobe) switch
        {
            (null, null) => null,
            (not null, not null) => $"{ffmpeg} {ffprobe}",
            _ => ffmpeg ?? ffprobe,
        };
    }
}
