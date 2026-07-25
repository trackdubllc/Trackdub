using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeTextRefinementEngine(
    Func<TextRefinementRequest, TextRefinementInputSegment, string>? textFactory = null,
    Func<TextRefinementRequest, TextRefinementInputSegment, bool>? acceptFactory = null)
    : ITextRefinementEngine
{
    private readonly Func<TextRefinementRequest, TextRefinementInputSegment, string> textFactory =
        textFactory ?? DefaultTextFactory;

    private readonly Func<TextRefinementRequest, TextRefinementInputSegment, bool> acceptFactory =
        acceptFactory ?? DefaultAcceptFactory;

    public string EngineFamily => "fake-text-refinement";

    public Task<IReadOnlyList<RefinedTextSegment>> RefineAsync(
        TextRefinementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Segments);

        IReadOnlyList<RefinedTextSegment> refinedSegments = request.Segments
            .OrderBy(segment => segment.Index)
            .Select(segment =>
            {
                string original = segment.Text;
                string refined = textFactory(request, segment);
                bool accepted = acceptFactory(request, segment) &&
                                !string.Equals(original, refined, StringComparison.Ordinal);
                return new RefinedTextSegment(
                    segment.Index,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    original,
                    refined,
                    accepted ? refined : original,
                    accepted,
                    accepted ? TextRefinementGuardStatus.Accepted : TextRefinementGuardStatus.Unchanged,
                    accepted
                        ? [TextRefinementCorrectionCodes.ModelPolishApplied]
                        : [TextRefinementCorrectionCodes.FallbackUnchanged]);
            })
            .ToArray();

        return Task.FromResult(refinedSegments);
    }

    private static string DefaultTextFactory(TextRefinementRequest request, TextRefinementInputSegment segment) =>
        $"{segment.Text.TrimEnd('.')}.";

    private static bool DefaultAcceptFactory(TextRefinementRequest request, TextRefinementInputSegment segment) => true;
}
