using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Runtime.Routing;

namespace Trackdub.Inference.Onnx.QwenTextRefinement;

public sealed class RoutedTextRefinementEngine(IEnumerable<ITextRefinementEngine> engines)
    : ITextRefinementEngine, IStageRuntimeExecutionReporter
{
    private readonly IReadOnlyList<ITextRefinementEngine> engines =
        (engines ?? throw new ArgumentNullException(nameof(engines))).ToArray();

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public string EngineFamily => SelectEngine().EngineFamily;

    public async Task<IReadOnlyList<RefinedTextSegment>> RefineAsync(
        TextRefinementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ITextRefinementEngine selectedEngine = SelectEngine(request.PreferredModelAlias);
        IReadOnlyList<RefinedTextSegment> refinedSegments = await selectedEngine
            .RefineAsync(request, cancellationToken)
            .ConfigureAwait(false);

        LastExecutionSummary = selectedEngine is IStageRuntimeExecutionReporter reporter
            ? reporter.LastExecutionSummary
            : null;
        return refinedSegments;
    }

    private ITextRefinementEngine SelectEngine(string? preferredModelAlias = null)
    {
        if (engines.Count == 0)
        {
            throw new InvalidOperationException("No text refinement inference engines are registered.");
        }

        if (!string.IsNullOrWhiteSpace(preferredModelAlias))
        {
            string normalizedAlias = preferredModelAlias.Trim();
            ITextRefinementEngine? aliasMatch = engines.FirstOrDefault(candidate =>
                string.Equals(candidate.EngineFamily, normalizedAlias, StringComparison.OrdinalIgnoreCase));
            if (aliasMatch is not null)
            {
                return aliasMatch;
            }
        }

        ITextRefinementEngine? qwenEngine = engines.FirstOrDefault(candidate =>
            string.Equals(candidate.EngineFamily, QwenTextRefinementEngine.EngineFamilyName, StringComparison.OrdinalIgnoreCase));
        return qwenEngine ?? engines[0];
    }
}
