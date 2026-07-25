using System.Globalization;
using System.Text;
using Trackdub.Contracts.Pipeline;
using Trackdub.Media.Process;

namespace Trackdub.Media.Muxing;

public sealed class FfmpegVideoRecomposer : IVideoRecomposer
{
    private readonly IProcessRunner processRunner;
    private readonly FfmpegToolResolver toolResolver;

    public FfmpegVideoRecomposer(string? ffmpegPath = null)
        : this(new ProcessRunner(), ffmpegPath)
    {
    }

    internal FfmpegVideoRecomposer(IProcessRunner processRunner, string? ffmpegPath = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        toolResolver = new FfmpegToolResolver(ffmpegPath);
    }

    public async Task<VideoRecompositionResult> RecomposeAsync(
        ResolvedVideoRecompositionPlan plan,
        string outputVideoPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputVideoPath);

        string sourceVideoPath = Path.GetFullPath(plan.SourceVideoPath);
        if (!File.Exists(sourceVideoPath))
        {
            throw new FileNotFoundException("Source video file was not found.", sourceVideoPath);
        }

        if (plan.PatchedTurns.Count == 0)
        {
            throw new InvalidOperationException("Video recomposition requires at least one patched turn.");
        }

        foreach (ResolvedRecomposedTurn turn in plan.PatchedTurns)
        {
            if (!File.Exists(turn.PatchedClipPath))
            {
                throw new FileNotFoundException("Patched lip-synthesis clip was not found.", turn.PatchedClipPath);
            }

            if (turn.End <= turn.Start)
            {
                throw new InvalidOperationException(
                    $"Patched turn end must be after start ({turn.Start}..{turn.End}).");
            }
        }

        string? outputDirectory = Path.GetDirectoryName(outputVideoPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        if (File.Exists(outputVideoPath))
        {
            File.Delete(outputVideoPath);
        }

        string ffmpegPath = toolResolver.ResolveFfmpegPath();
        IReadOnlyList<string> arguments = FfmpegVideoRecompositionCommandBuilder.BuildArguments(
            plan,
            outputVideoPath);
        ProcessResult result = await processRunner
            .RunAsync(ffmpegPath, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(FfmpegErrorFormatter.BuildFailureMessage(
                "ffmpeg lip-synthesis recomposition",
                result.ExitCode,
                result.StandardError));
        }

        if (!File.Exists(outputVideoPath))
        {
            throw new InvalidOperationException("ffmpeg completed without producing a recomposed export video.");
        }

        return new VideoRecompositionResult(
            outputVideoPath,
            [$"Composited {plan.PatchedTurns.Count} lip-synthesis repaired turn(s) into export video."]);
    }
}

internal static class FfmpegVideoRecompositionCommandBuilder
{
    public static IReadOnlyList<string> BuildArguments(
        ResolvedVideoRecompositionPlan plan,
        string outputVideoPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputVideoPath);

        var arguments = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            Path.GetFullPath(plan.SourceVideoPath)
        };

        foreach (ResolvedRecomposedTurn turn in plan.PatchedTurns)
        {
            arguments.Add("-i");
            arguments.Add(Path.GetFullPath(turn.PatchedClipPath));
        }

        arguments.Add("-filter_complex");
        arguments.Add(BuildFilterGraph(plan.PatchedTurns));
        arguments.Add("-map");
        arguments.Add("[vout]");
        arguments.Add("-an");
        arguments.Add("-c:v");
        arguments.Add("libx264");
        arguments.Add("-pix_fmt");
        arguments.Add("yuv420p");
        arguments.Add(Path.GetFullPath(outputVideoPath));
        return arguments;
    }

    internal static string BuildFilterGraph(IReadOnlyList<ResolvedRecomposedTurn> patchedTurns)
    {
        if (patchedTurns.Count == 0)
        {
            throw new InvalidOperationException("At least one patched turn is required.");
        }

        var builder = new StringBuilder();
        for (int index = 0; index < patchedTurns.Count; index++)
        {
            ResolvedRecomposedTurn turn = patchedTurns[index];
            double startSeconds = turn.Start.TotalSeconds;
            builder.Append(CultureInfo.InvariantCulture, $"[{index + 1}:v]setpts=PTS+{startSeconds}/TB[p{index}];");
        }

        string currentLabel = "[0:v]";
        for (int index = 0; index < patchedTurns.Count; index++)
        {
            ResolvedRecomposedTurn turn = patchedTurns[index];
            string nextLabel = index == patchedTurns.Count - 1 ? "[vout]" : $"[v{index}]";
            double startSeconds = turn.Start.TotalSeconds;
            double endSeconds = turn.End.TotalSeconds;
            builder.Append(CultureInfo.InvariantCulture, $"{currentLabel}[p{index}]overlay=0:0:enable='between(t\\,{startSeconds}\\,{endSeconds})'{nextLabel};");
            currentLabel = nextLabel;
        }

        return builder.ToString().TrimEnd(';');
    }
}
