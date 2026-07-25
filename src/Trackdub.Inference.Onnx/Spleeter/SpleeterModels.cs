using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Spleeter;

internal sealed record SpleeterSeparatorRequest(
    string ModelRootPath,
    StageRuntimePlan Plan,
    float[] Left,
    float[] Right,
    int SampleRate);

internal sealed record SpleeterSeparation(
    float[] Vocals,
    float[] Accompaniment,
    int SampleRate,
    int ChunkCount,
    IReadOnlyDictionary<string, string>? Metadata = null);

internal interface ISpleeterSeparator
{
    Task<SpleeterSeparation> SeparateAsync(
        SpleeterSeparatorRequest request,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken);
}
