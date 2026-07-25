using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.SepFormer;

internal sealed record SepFormerSeparatorRequest(
    string ModelRootPath,
    StageRuntimePlan Plan,
    float[] Samples,
    int SampleRate);

internal sealed record SepFormerSeparation(
    float[] Source0,
    float[] Source1,
    int SampleRate,
    int ChunkCount,
    bool PermutationWarning = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

internal sealed record SepFormerRegionRequest(
    string ModelRootPath,
    StageRuntimePlan Plan,
    float[] Samples,
    int SampleRate);

internal interface ISepFormerSeparator
{
    Task<SepFormerSeparation> SeparateAsync(
        SepFormerSeparatorRequest request,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken);

    Task<SepFormerSeparation> SeparateRegionAsync(
        SepFormerRegionRequest request,
        CancellationToken cancellationToken);
}
