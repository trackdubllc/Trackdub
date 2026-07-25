using Trackdub.Domain.LipSynthesis;

namespace Trackdub.Contracts.Pipeline;

public interface IVideoRecomposer
{
    Task<VideoRecompositionResult> RecomposeAsync(
        ResolvedVideoRecompositionPlan plan,
        string outputVideoPath,
        CancellationToken cancellationToken);
}

public sealed record ResolvedVideoRecompositionPlan(
    string SourceVideoPath,
    IReadOnlyList<ResolvedRecomposedTurn> PatchedTurns);

public sealed record ResolvedRecomposedTurn(
    TimeSpan Start,
    TimeSpan End,
    string PatchedClipPath);

public sealed record VideoRecompositionResult(
    string OutputPath,
    IReadOnlyList<string> Warnings);
