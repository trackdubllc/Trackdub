using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeVideoRecomposer : IVideoRecomposer
{
    private readonly List<(ResolvedVideoRecompositionPlan Plan, string OutputPath)> calls = [];

    public IReadOnlyList<(ResolvedVideoRecompositionPlan Plan, string OutputPath)> Calls => calls;

    public Task<VideoRecompositionResult> RecomposeAsync(
        ResolvedVideoRecompositionPlan plan,
        string outputVideoPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputVideoPath);
        cancellationToken.ThrowIfCancellationRequested();

        calls.Add((plan, outputVideoPath));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputVideoPath))!);
        File.Copy(plan.SourceVideoPath, outputVideoPath, overwrite: true);
        return Task.FromResult(new VideoRecompositionResult(
            outputVideoPath,
            [$"Composited {plan.PatchedTurns.Count} lip-synthesis repaired turn(s) into export video."]));
    }
}
