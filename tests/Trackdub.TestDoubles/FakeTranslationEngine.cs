using Trackdub.Contracts.Pipeline;
using System.Globalization;

namespace Trackdub.TestDoubles;

public sealed class FakeTranslationEngine(
    Func<TranslationRequest, TranslationInputSegment, string>? textFactory = null,
    Func<TranslationRequest, TranslationExecutionMetadata?>? metadataFactory = null)
    : ITranslationEngine, ITranslationExecutionMetadataReporter
{
    private readonly Func<TranslationRequest, TranslationInputSegment, string> textFactory = textFactory ?? DefaultTextFactory;
    private readonly Func<TranslationRequest, TranslationExecutionMetadata?> metadataFactory = metadataFactory ?? DefaultMetadataFactory;

    public TranslationExecutionMetadata? LastExecutionMetadata { get; private set; }

    public Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Segments);

        LastExecutionMetadata = metadataFactory(request);
        IReadOnlyList<TranslatedTextSegment> translatedSegments = request.Segments
            .OrderBy(segment => segment.Index)
            .Select(segment => new TranslatedTextSegment(
                segment.Index,
                segment.StartSeconds,
                segment.EndSeconds,
                ApplyGlossaryHints(textFactory(request, segment), request.GlossaryHints, segment)))
            .ToArray();

        return Task.FromResult(translatedSegments);
    }

    private static string DefaultTextFactory(TranslationRequest request, TranslationInputSegment segment) =>
        request.TargetLanguage switch
        {
            "es" => $"Segmento generado {segment.Index + 1}.",
            "en" => $"Generated translation {segment.Index + 1}.",
            _ => $"[TRANSLATED] {segment.Text}"
        };

    private static TranslationExecutionMetadata? DefaultMetadataFactory(TranslationRequest request) => null;

    private static string ApplyGlossaryHints(
        string text,
        IReadOnlyList<TranslationGlossaryHint>? glossaryHints,
        TranslationInputSegment segment)
    {
        if (glossaryHints is null || glossaryHints.Count == 0)
        {
            return text;
        }

        string result = text;
        SpanReplacement[] replacements = glossaryHints
            .Where(static hint => hint.SourceMatches is { Count: > 0 })
            .SelectMany(hint => hint.SourceMatches!
                .Where(match => match.SegmentIndex == segment.Index)
                .Select(match => new SpanReplacement(
                    match.StartTextElementIndex,
                    match.TextElementLength,
                    hint.TargetTerm)))
            .OrderBy(replacement => replacement.StartTextElementIndex)
            .ThenByDescending(replacement => replacement.TextElementLength)
            .Aggregate(
                new List<SpanReplacement>(),
                static (selected, replacement) =>
                {
                    int replacementEnd = replacement.StartTextElementIndex + replacement.TextElementLength;
                    bool overlapsSelected = selected.Any(selectedReplacement =>
                    {
                        int selectedEnd = selectedReplacement.StartTextElementIndex + selectedReplacement.TextElementLength;
                        return replacement.StartTextElementIndex < selectedEnd &&
                               replacementEnd > selectedReplacement.StartTextElementIndex;
                    });
                    if (!overlapsSelected)
                    {
                        selected.Add(replacement);
                    }

                    return selected;
                })
            .OrderByDescending(replacement => replacement.StartTextElementIndex)
            .ToArray();
        foreach (SpanReplacement replacement in replacements)
        {
            result = ReplaceTextElementSpan(result, replacement);
        }

        foreach (TranslationGlossaryHint hint in glossaryHints)
        {
            if (hint.SourceMatches is { Count: > 0 })
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(hint.SourceTerm))
            {
                continue;
            }

            result = result.Replace(
                hint.SourceTerm,
                hint.TargetTerm,
                hint.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string ReplaceTextElementSpan(
        string text,
        SpanReplacement replacement)
    {
        if (replacement.StartTextElementIndex < 0 || replacement.TextElementLength <= 0)
        {
            return text;
        }

        int[] textElementIndexes = StringInfo.ParseCombiningCharacters(text);
        int endTextElementIndex = replacement.StartTextElementIndex + replacement.TextElementLength;
        if (replacement.StartTextElementIndex >= textElementIndexes.Length ||
            endTextElementIndex > textElementIndexes.Length)
        {
            return text;
        }

        int startCharIndex = textElementIndexes[replacement.StartTextElementIndex];
        int endCharIndex = endTextElementIndex == textElementIndexes.Length
            ? text.Length
            : textElementIndexes[endTextElementIndex];
        return string.Concat(
            text.AsSpan(0, startCharIndex),
            replacement.TargetTerm,
            text.AsSpan(endCharIndex));
    }

    private sealed record SpanReplacement(
        int StartTextElementIndex,
        int TextElementLength,
        string TargetTerm);
}
