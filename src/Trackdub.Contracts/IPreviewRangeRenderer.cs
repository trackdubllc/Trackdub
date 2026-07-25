using Trackdub.Domain.Mixing;

namespace Trackdub.Contracts;

public interface IPreviewRangeRenderer
{
    Task<PreviewRangeRenderResult> RenderAsync(
        PreviewRangeRenderRequest request,
        CancellationToken cancellationToken);
}

public sealed record PreviewRangeRenderRequest(
    MixPlan MixPlan,
    double StartSeconds,
    double EndSeconds,
    string OutputPath);

public sealed record PreviewRangeRenderResult(
    string OutputPath,
    double DurationSeconds,
    int SampleRate,
    int ChannelCount);
