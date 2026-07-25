using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Transcripts;

public sealed class GlossaryTermMatcher : IGlossaryTermMatcher
{
    private readonly GlossaryTermNormalizer normalizer;
    private readonly IGlossaryAnalyzerCatalog analyzerCatalog;

    public GlossaryTermMatcher()
        : this(new GlossaryTermNormalizer(), GlossaryAnalyzerCatalog.Empty)
    {
    }

    public GlossaryTermMatcher(IGlossaryAnalyzerCatalog analyzerCatalog)
        : this(new GlossaryTermNormalizer(), analyzerCatalog)
    {
    }

    internal GlossaryTermMatcher(
        GlossaryTermNormalizer normalizer,
        IGlossaryAnalyzerCatalog analyzerCatalog)
    {
        this.normalizer = normalizer;
        this.analyzerCatalog = analyzerCatalog;
    }

    public IReadOnlyList<TranslationGlossaryHint> BuildHints(
        string sourceLanguage,
        IReadOnlyList<TranslationInputSegment> segments,
        IReadOnlyList<GlossaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return [];
        }

        if (!normalizer.SupportsSourceLanguage(sourceLanguage))
        {
            return entries
                .OrderBy(entry => entry.SourceTerm, StringComparer.Ordinal)
                .ThenBy(entry => entry.TargetTerm, StringComparer.Ordinal)
                .Select(entry => new TranslationGlossaryHint(
                    entry.SourceTerm,
                    entry.TargetTerm,
                    entry.IsCaseSensitive))
                .ToArray();
        }

        GlossaryCandidate[] scannerCandidates = CreateScannerCandidates(sourceLanguage, entries);
        IGlossaryLanguageAnalyzer? analyzer = analyzerCatalog.Resolve(sourceLanguage);
        AnalyzerGlossaryCandidate[] analyzerCandidates = analyzer is null
            ? []
            : CreateAnalyzerCandidates(sourceLanguage, entries, analyzer);

        if (scannerCandidates.Length == 0 && analyzerCandidates.Length == 0)
        {
            return [];
        }

        var matchesByEntryId = new Dictionary<Guid, List<TranslationGlossarySourceMatch>>();
        foreach (TranslationInputSegment segment in segments.OrderBy(segment => segment.Index))
        {
            var occupiedSpans = new List<TextElementSpan>();
            if (analyzer is not null && analyzerCandidates.Length > 0)
            {
                MatchAnalyzerSegment(
                    sourceLanguage,
                    segment,
                    analyzer,
                    analyzerCandidates,
                    matchesByEntryId,
                    occupiedSpans);
            }

            if (scannerCandidates.Length > 0)
            {
                MatchScannerSegment(
                    sourceLanguage,
                    segment,
                    scannerCandidates,
                    matchesByEntryId,
                    occupiedSpans);
            }
        }

        return entries
            .Where(entry => matchesByEntryId.ContainsKey(entry.Id))
            .OrderBy(entry => entry.SourceTerm, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetTerm, StringComparer.Ordinal)
            .Select(entry => new TranslationGlossaryHint(
                entry.SourceTerm,
                entry.TargetTerm,
                entry.IsCaseSensitive,
                matchesByEntryId[entry.Id]))
            .ToArray();
    }

    private GlossaryCandidate[] CreateScannerCandidates(
        string sourceLanguage,
        IReadOnlyList<GlossaryEntry> entries) =>
        entries
            .Select(entry => new GlossaryCandidate(
                entry,
                normalizer.Normalize(sourceLanguage, entry.SourceTerm, entry.IsCaseSensitive).TextElements))
            .Where(candidate => candidate.SourceTextElements.Count > 0)
            .OrderByDescending(candidate => candidate.SourceTextElements.Count)
            .ThenBy(candidate => candidate.Entry.SourceTerm, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Entry.TargetTerm, StringComparer.Ordinal)
            .ToArray();

    private AnalyzerGlossaryCandidate[] CreateAnalyzerCandidates(
        string sourceLanguage,
        IReadOnlyList<GlossaryEntry> entries,
        IGlossaryLanguageAnalyzer analyzer)
    {
        var candidates = new List<AnalyzerGlossaryCandidate>();
        foreach (GlossaryEntry entry in entries)
        {
            IReadOnlyList<GlossaryAnalysisToken> tokens;
            try
            {
                tokens = analyzer.Analyze(sourceLanguage, entry.SourceTerm);
            }
            catch
            {
                continue;
            }

            IReadOnlyList<HashSet<string>> tokenAlternatives = tokens
                .Where(IsUsableAnalysisToken)
                .Select(token => GetTokenAlternatives(sourceLanguage, token, entry.IsCaseSensitive))
                .Where(alternatives => alternatives.Count > 0)
                .ToArray();
            if (tokenAlternatives.Count == 0)
            {
                continue;
            }

            int sourceTextElementLength = normalizer
                .Normalize(sourceLanguage, entry.SourceTerm, entry.IsCaseSensitive)
                .OriginalTextElements.Count;
            candidates.Add(new AnalyzerGlossaryCandidate(
                entry,
                tokenAlternatives,
                sourceTextElementLength));
        }

        return candidates
            .OrderByDescending(candidate => candidate.SourceTokenAlternatives.Count)
            .ThenByDescending(candidate => candidate.SourceTextElementLength)
            .ThenBy(candidate => candidate.Entry.SourceTerm, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Entry.TargetTerm, StringComparer.Ordinal)
            .ToArray();
    }

    private void MatchAnalyzerSegment(
        string sourceLanguage,
        TranslationInputSegment segment,
        IGlossaryLanguageAnalyzer analyzer,
        IReadOnlyList<AnalyzerGlossaryCandidate> candidates,
        Dictionary<Guid, List<TranslationGlossarySourceMatch>> matchesByEntryId,
        List<TextElementSpan> occupiedSpans)
    {
        IReadOnlyList<AnalyzerSegmentToken> tokens;
        try
        {
            tokens = analyzer
                .Analyze(sourceLanguage, segment.Text)
                .Where(IsUsableAnalysisToken)
                .Select(token => new AnalyzerSegmentToken(
                    token,
                    CaseInsensitiveAlternatives: GetTokenAlternatives(sourceLanguage, token, isCaseSensitive: false),
                    CaseSensitiveAlternatives: GetTokenAlternatives(sourceLanguage, token, isCaseSensitive: true)))
                .ToArray();
        }
        catch
        {
            return;
        }

        if (tokens.Count == 0)
        {
            return;
        }

        NormalizedGlossaryText segmentText = normalizer.Normalize(
            sourceLanguage,
            segment.Text,
            isCaseSensitive: false);
        int index = 0;
        while (index < tokens.Count)
        {
            AnalyzerGlossaryCandidate? match = candidates.FirstOrDefault(candidate =>
                IsAnalyzerMatchAt(tokens, index, candidate));
            if (match is null)
            {
                index++;
                continue;
            }

            AddAnalyzerMatch(segment, tokens, index, match, segmentText, matchesByEntryId, occupiedSpans);
            index += match.SourceTokenAlternatives.Count;
        }
    }

    private static void AddAnalyzerMatch(
        TranslationInputSegment segment,
        IReadOnlyList<AnalyzerSegmentToken> tokens,
        int tokenIndex,
        AnalyzerGlossaryCandidate match,
        NormalizedGlossaryText segmentText,
        Dictionary<Guid, List<TranslationGlossarySourceMatch>> matchesByEntryId,
        List<TextElementSpan> occupiedSpans)
    {
        if (!matchesByEntryId.TryGetValue(match.Entry.Id, out List<TranslationGlossarySourceMatch>? matches))
        {
            matches = [];
            matchesByEntryId[match.Entry.Id] = matches;
        }

        int originalStartTextElementIndex = tokens[tokenIndex].Token.StartTextElementIndex;
        GlossaryAnalysisToken endToken = tokens[tokenIndex + match.SourceTokenAlternatives.Count - 1].Token;
        int originalEndTextElementIndex = endToken.StartTextElementIndex + endToken.TextElementLength;
        int originalTextElementLength = originalEndTextElementIndex - originalStartTextElementIndex;
        var span = new TextElementSpan(originalStartTextElementIndex, originalTextElementLength);
        matches.Add(new TranslationGlossarySourceMatch(
            segment.Index,
            span.StartTextElementIndex,
            span.TextElementLength,
            string.Concat(segmentText.OriginalTextElements
                .Skip(span.StartTextElementIndex)
                .Take(span.TextElementLength))));
        occupiedSpans.Add(span);
    }

    private void MatchScannerSegment(
        string sourceLanguage,
        TranslationInputSegment segment,
        IReadOnlyList<GlossaryCandidate> candidates,
        Dictionary<Guid, List<TranslationGlossarySourceMatch>> matchesByEntryId,
        List<TextElementSpan> occupiedSpans)
    {
        NormalizedGlossaryText caseInsensitiveSegmentText = normalizer.Normalize(
            sourceLanguage,
            segment.Text,
            isCaseSensitive: false);
        NormalizedGlossaryText caseSensitiveSegmentText = normalizer.Normalize(
            sourceLanguage,
            segment.Text,
            isCaseSensitive: true);
        int index = 0;
        while (index < caseInsensitiveSegmentText.TextElements.Count)
        {
            GlossaryCandidate? match = candidates.FirstOrDefault(candidate =>
                IsMatchAt(
                    GetComparableTextElements(caseInsensitiveSegmentText, caseSensitiveSegmentText, candidate),
                    index,
                    candidate));
            if (match is null)
            {
                index++;
                continue;
            }

            TextElementSpan span = GetScannerOriginalSpan(
                caseInsensitiveSegmentText,
                caseSensitiveSegmentText,
                index,
                match);
            if (OverlapsAny(occupiedSpans, span))
            {
                index++;
                continue;
            }

            AddScannerMatch(
                segment,
                caseInsensitiveSegmentText,
                span,
                match,
                matchesByEntryId);
            occupiedSpans.Add(span);
            index += match.SourceTextElements.Count;
        }
    }

    private static void AddScannerMatch(
        TranslationInputSegment segment,
        NormalizedGlossaryText caseInsensitiveSegmentText,
        TextElementSpan span,
        GlossaryCandidate match,
        Dictionary<Guid, List<TranslationGlossarySourceMatch>> matchesByEntryId)
    {
        if (!matchesByEntryId.TryGetValue(match.Entry.Id, out List<TranslationGlossarySourceMatch>? matches))
        {
            matches = [];
            matchesByEntryId[match.Entry.Id] = matches;
        }

        matches.Add(new TranslationGlossarySourceMatch(
            segment.Index,
            span.StartTextElementIndex,
            span.TextElementLength,
            string.Concat(caseInsensitiveSegmentText.OriginalTextElements
                .Skip(span.StartTextElementIndex)
                .Take(span.TextElementLength))));
    }

    private bool IsAnalyzerMatchAt(
        IReadOnlyList<AnalyzerSegmentToken> tokens,
        int startIndex,
        AnalyzerGlossaryCandidate candidate)
    {
        if (startIndex + candidate.SourceTokenAlternatives.Count > tokens.Count)
        {
            return false;
        }

        for (int i = 0; i < candidate.SourceTokenAlternatives.Count; i++)
        {
            HashSet<string> segmentAlternatives = candidate.Entry.IsCaseSensitive
                ? tokens[startIndex + i].CaseSensitiveAlternatives
                : tokens[startIndex + i].CaseInsensitiveAlternatives;
            if (!segmentAlternatives.Overlaps(candidate.SourceTokenAlternatives[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMatchAt(
        IReadOnlyList<NormalizedGlossaryTextElement> segmentTextElements,
        int startIndex,
        GlossaryCandidate candidate)
    {
        if (startIndex + candidate.SourceTextElements.Count > segmentTextElements.Count)
        {
            return false;
        }

        for (int i = 0; i < candidate.SourceTextElements.Count; i++)
        {
            if (!string.Equals(
                    segmentTextElements[startIndex + i].Value,
                    candidate.SourceTextElements[i].Value,
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
        GlossaryCandidate candidate) =>
        candidate.Entry.IsCaseSensitive
            ? caseSensitiveSegmentText.TextElements
            : caseInsensitiveSegmentText.TextElements;

    private static TextElementSpan GetScannerOriginalSpan(
        NormalizedGlossaryText caseInsensitiveSegmentText,
        NormalizedGlossaryText caseSensitiveSegmentText,
        int index,
        GlossaryCandidate match)
    {
        IReadOnlyList<NormalizedGlossaryTextElement> comparableTextElements = GetComparableTextElements(
            caseInsensitiveSegmentText,
            caseSensitiveSegmentText,
            match);
        int originalStartTextElementIndex = comparableTextElements[index].OriginalStartTextElementIndex;
        int originalEndTextElementIndex =
            comparableTextElements[index + match.SourceTextElements.Count - 1].OriginalStartTextElementIndex
            + comparableTextElements[index + match.SourceTextElements.Count - 1].OriginalTextElementLength;
        return new TextElementSpan(
            originalStartTextElementIndex,
            originalEndTextElementIndex - originalStartTextElementIndex);
    }

    private static bool OverlapsAny(
        IReadOnlyList<TextElementSpan> occupiedSpans,
        TextElementSpan candidate) =>
        occupiedSpans.Any(span =>
            span.StartTextElementIndex < candidate.EndTextElementIndex
            && candidate.StartTextElementIndex < span.EndTextElementIndex);

    private HashSet<string> GetTokenAlternatives(
        string sourceLanguage,
        GlossaryAnalysisToken token,
        bool isCaseSensitive)
    {
        var alternatives = new HashSet<string>(StringComparer.Ordinal);
        AddNormalizedAlternative(alternatives, sourceLanguage, token.SurfaceText, isCaseSensitive);
        AddNormalizedAlternative(alternatives, sourceLanguage, token.NormalizedText, isCaseSensitive);
        AddNormalizedAlternative(alternatives, sourceLanguage, token.Lemma, isCaseSensitive);
        return alternatives;
    }

    private void AddNormalizedAlternative(
        HashSet<string> alternatives,
        string sourceLanguage,
        string? value,
        bool isCaseSensitive)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string normalizedValue = string.Concat(normalizer
            .Normalize(sourceLanguage, value, isCaseSensitive)
            .TextElements
            .Select(element => element.Value));
        if (!string.IsNullOrWhiteSpace(normalizedValue))
        {
            alternatives.Add(normalizedValue);
        }
    }

    private static bool IsUsableAnalysisToken(GlossaryAnalysisToken token) =>
        token.StartTextElementIndex >= 0
        && token.TextElementLength > 0
        && (!string.IsNullOrWhiteSpace(token.SurfaceText)
            || !string.IsNullOrWhiteSpace(token.NormalizedText)
            || !string.IsNullOrWhiteSpace(token.Lemma));

    private sealed record GlossaryCandidate(
        GlossaryEntry Entry,
        IReadOnlyList<NormalizedGlossaryTextElement> SourceTextElements);

    private sealed record AnalyzerGlossaryCandidate(
        GlossaryEntry Entry,
        IReadOnlyList<HashSet<string>> SourceTokenAlternatives,
        int SourceTextElementLength);

    private sealed record AnalyzerSegmentToken(
        GlossaryAnalysisToken Token,
        HashSet<string> CaseInsensitiveAlternatives,
        HashSet<string> CaseSensitiveAlternatives);

    private readonly record struct TextElementSpan(
        int StartTextElementIndex,
        int TextElementLength)
    {
        public int EndTextElementIndex => StartTextElementIndex + TextElementLength;
    }
}
