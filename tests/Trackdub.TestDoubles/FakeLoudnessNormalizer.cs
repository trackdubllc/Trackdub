using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeLoudnessNormalizer : ILoudnessNormalizer
{
    private readonly List<LoudnessNormalizationRequest> calls = [];
    private readonly List<LoudnessAnalysisRequest> analysisCalls = [];
    private readonly Queue<Func<LoudnessAnalysisRequest, LoudnessAnalysisResult>> analysisResults = [];

    public IReadOnlyList<LoudnessNormalizationRequest> Calls => calls;

    public IReadOnlyList<LoudnessAnalysisRequest> AnalysisCalls => analysisCalls;

    public double? AchievedLufs { get; set; }

    public void EnqueueAnalysisResult(double integratedLufs) =>
        analysisResults.Enqueue(request => new LoudnessAnalysisResult(request.InputPath, integratedLufs, Warnings: []));

    public void EnqueueAnalysisFailure(Exception exception) =>
        analysisResults.Enqueue(_ => throw exception);

    public Task<LoudnessNormalizationResult> NormalizeAsync(
        LoudnessNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add(request);
        return Task.FromResult(new LoudnessNormalizationResult(
            request.OutputPath,
            request.TargetLufs,
            AchievedLufs ?? request.TargetLufs,
            Warnings: []));
    }

    public Task<LoudnessAnalysisResult> AnalyzeAsync(
        LoudnessAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        analysisCalls.Add(request);
        if (analysisResults.TryDequeue(out Func<LoudnessAnalysisRequest, LoudnessAnalysisResult>? resultFactory))
        {
            return Task.FromResult(resultFactory(request));
        }

        return Task.FromResult(new LoudnessAnalysisResult(request.InputPath, AchievedLufs ?? ExportLoudnessTargets.OnlineLufs, Warnings: []));
    }
}
