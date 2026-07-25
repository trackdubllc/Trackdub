using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.TestDoubles;

public sealed class FakeSpleeterStemSeparationEngine : IStemSeparationEngineAdapter, IStageRuntimeExecutionReporter
{
    public string EngineFamily => SpleeterStemSeparationEngine.EngineFamilyName;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public Task<StemSeparationResult> SeparateAsync(
        StemSeparationRequest request,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        LastExecutionSummary = new StageRuntimeExecutionSummary(
            "cpu",
            "cpu",
            "spleeter-onnx",
            "spleeter",
            "default",
            "Fake execution");

        return Task.FromResult(new StemSeparationResult(
            1.0,
            44100,
            1,
            new Dictionary<string, string>()));
    }
}
