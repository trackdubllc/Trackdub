using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// Locates glossary target-term matches inside translated segment text using the same
/// normalization rules as source-term matching (case sensitivity, accent folding, unspaced scripts).
/// </summary>
public sealed class GlossaryTargetTermMatcher : IGlossaryTargetTermMatcher
{
    private readonly GlossaryTermNormalizer normalizer = new();

    /// <summary>
    /// Returns non-overlapping character spans for the longest target-term matches found in
    /// <paramref name="translatedText"/> for the supplied glossary entries.
    /// </summary>
    public IReadOnlyList<GlossaryTextHighlightSpan> FindHighlightSpans(
        string targetLanguage,
        string translatedText,
        IReadOnlyList<GlossaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (string.IsNullOrEmpty(translatedText) || entries.Count == 0)
        {
            return [];
        }

        TargetTermCandidate[] candidates = PrepareTargetTermCandidates(targetLanguage, entries);
        if (candidates.Length == 0)
        {
            return [];
        }

        NormalizedGlossaryText caseInsensitiveSegmentText = normalizer.Normalize(
            targetLanguage,
            translatedText,
            isCaseSensitive: false);
        NormalizedGlossaryText caseSensitiveSegmentText = normalizer.Normalize(
            targetLanguage,
            translatedText,
            isCaseSensitive: true);

        if (caseInsensitiveSegmentText.TextElements.Count == 0)
        {
            return [];
        }

        return CollectHighlightSpans(
            candidates,
            caseInsensitiveSegmentText,
            caseSensitiveSegmentText);
    }

    private TargetTermCandidate[] PrepareTargetTermCandidates(
        string targetLanguage,
        IReadOnlyList<GlossaryEntry> entries) =>
        entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TargetTerm))
            .Select(entry => new TargetTermCandidate(
                entry,
                normalizer.Normalize(targetLanguage, entry.TargetTerm, entry.IsCaseSensitive).TextElements))
            .Where(candidate => candidate.TargetTextElements.Count > 0)
            .OrderByDescending(candidate => candidate.TargetTextElements.Count)
            .ThenBy(candidate => candidate.Entry.TargetTerm, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<GlossaryTextHighlightSpan> CollectHighlightSpans(
        TargetTermCandidate[] candidates,
        NormalizedGlossaryText caseInsensitiveSegmentText,
        NormalizedGlossaryText caseSensitiveSegmentText)
    {
        var occupiedSpans = new List<TextElementSpan>();
        var highlightSpans = new List<GlossaryTextHighlightSpan>();
        int index = 0;
        while (index < caseInsensitiveSegmentText.TextElements.Count)
        {
            TargetTermCandidate? match = candidates.FirstOrDefault(candidate =>
                IsMatchAt(
                    GetComparableTextElements(caseInsensitiveSegmentText, caseSensitiveSegmentText, candidate),
                    index,
                    candidate));
            if (match is null)
            {
                index++;
                continue;
            }

            TextElementSpan span = GetOriginalSpan(
                caseInsensitiveSegmentText,
                caseSensitiveSegmentText,
                index,
                match);
            if (span.TextElementLength <= 0)
            {
                index++;
                continue;
            }

            if (OverlapsAny(occupiedSpans, span))
            {
                index++;
                continue;
            }

            GlossaryTextHighlightSpan characterSpan = ToCharacterSpan(
                caseInsensitiveSegmentText.OriginalTextElements,
                span);
            if (characterSpan.Length > 0)
            {
                highlightSpans.Add(characterSpan);
            }

            occupiedSpans.Add(span);
            index += match.TargetTextElements.Count;
        }

        return highlightSpans
            .OrderBy(span => span.Start)
            .ToArray();
    }

    /// <summary>
    /// Returns whether <paramref name="candidate"/> matches normalized segment text at
    /// <paramref name="startIndex"/> using ordinal element comparison.
    /// Each normalized text element corresponds to one original grapheme cluster; comparison uses
    /// <see cref="StringComparison.Ordinal"/> because normalization already applied casing and accent rules.
    /// </summary>
    private static bool IsMatchAt(
        IReadOnlyList<NormalizedGlossaryTextElement> segmentTextElements,
        int startIndex,
        TargetTermCandidate candidate)
    {
        if (startIndex < 0
            || candidate.TargetTextElements.Count <= 0
            || startIndex + candidate.TargetTextElements.Count > segmentTextElements.Count)
        {
            return false;
        }

        for (int i = 0; i < candidate.TargetTextElements.Count; i++)
        {
            if (!string.Equals(
                    segmentTextElements[startIndex + i].Value,
                    candidate.TargetTextElements[i].Value,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<NormalizedGlossaryTextElement> GetComparableTextElements(
        NormalizedGlossaryText caseInsensitiveSegmentText,
        NormalizedGlossaryText caseSensitiveSegmentText,
        TargetTermCandidate candidate) =>
        candidate.Entry.IsCaseSensitive
            ? caseSensitiveSegmentText.TextElements
            : caseInsensitiveSegmentText.TextElements;

    /// <summary>
    /// Maps a normalized match position back to original grapheme-cluster indices in
    /// <see cref="NormalizedGlossaryText.OriginalTextElements"/>.
    /// </summary>
    private static TextElementSpan GetOriginalSpan(
        NormalizedGlossaryText caseInsensitiveSegmentText,
        NormalizedGlossaryText caseSensitiveSegmentText,
        int index,
        TargetTermCandidate match)
    {
        IReadOnlyList<NormalizedGlossaryTextElement> comparableTextElements = GetComparableTextElements(
            caseInsensitiveSegmentText,
            caseSensitiveSegmentText,
            match);
        if (index < 0
            || match.TargetTextElements.Count <= 0
            || index + match.TargetTextElements.Count > comparableTextElements.Count)
        {
            return new TextElementSpan(0, 0);
        }

        int originalStartTextElementIndex = comparableTextElements[index].OriginalStartTextElementIndex;
        int originalEndTextElementIndex =
            comparableTextElements[index + match.TargetTextElements.Count - 1].OriginalStartTextElementIndex
            + comparableTextElements[index + match.TargetTextElements.Count - 1].OriginalTextElementLength;
        return new TextElementSpan(
            originalStartTextElementIndex,
            originalEndTextElementIndex - originalStartTextElementIndex);
    }

    /// <summary>
    /// Returns whether two occupied spans share any original grapheme-cluster index.
    /// Overlap is tracked in text-element space because matching also runs in that space.
    /// </summary>
    private static bool OverlapsAny(
        IReadOnlyList<TextElementSpan> occupiedSpans,
        TextElementSpan candidate) =>
        occupiedSpans.Any(span =>
            span.StartTextElementIndex < candidate.EndTextElementIndex
            && candidate.StartTextElementIndex < span.EndTextElementIndex);

    /// <summary>
    /// Converts a grapheme-cluster span into UTF-16 character offsets for UI highlighting by summing
    /// <see cref="string.Length"/> of each original text element. Returns a zero-length span when
    /// bounds are invalid.
    /// </summary>
    private static GlossaryTextHighlightSpan ToCharacterSpan(
        IReadOnlyList<string> originalTextElements,
        TextElementSpan span)
    {
        if (span.StartTextElementIndex < 0
            || span.EndTextElementIndex > originalTextElements.Count
            || span.TextElementLength <= 0)
        {
            return new GlossaryTextHighlightSpan(0, 0);
        }

        int start = 0;
        for (int i = 0; i < span.StartTextElementIndex; i++)
        {
            start += originalTextElements[i].Length;
        }

        int length = 0;
        for (int i = span.StartTextElementIndex; i < span.EndTextElementIndex; i++)
        {
            length += originalTextElements[i].Length;
        }

        return new GlossaryTextHighlightSpan(start, length);
    }

    private sealed record TargetTermCandidate(
        GlossaryEntry Entry,
        IReadOnlyList<NormalizedGlossaryTextElement> TargetTextElements);

    private readonly record struct TextElementSpan(
        int StartTextElementIndex,
        int TextElementLength)
    {
        public int EndTextElementIndex => StartTextElementIndex + TextElementLength;
    }
}
