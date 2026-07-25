using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// Aligns translated words to the source segment timeline using proportional
/// character-duration distribution.
/// <para>
/// When the source <see cref="TranscriptSegment"/> carries word-level ASR timings,
/// those word spans define the time budget. Translated words are then distributed
/// across that budget in proportion to their character count so that shorter words
/// are assigned shorter durations and longer words longer ones.
/// </para>
/// <para>
/// When the source segment has no word-level timings the translated segment's own
/// <see cref="TranslatedSegment.StartSeconds"/> / <see cref="TranslatedSegment.EndSeconds"/>
/// bounds serve as the time budget.
/// </para>
/// <para>
/// This implementation is deterministic and needs no inference model. Timings are
/// grounded in real ASR data rather than invented; no alignment is emitted when the
/// translated text cannot be split into at least one whitespace-delimited word.
/// </para>
/// </summary>
public sealed class ProportionalTranslatedWordAlignmentService : ITranslatedWordAlignmentService
{
    public Task<TranslatedWordAlignmentResult> AlignAsync(
        TranslatedWordAlignmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Step 1: tokenize the translated text.
        string[] tokens = TokenizeWords(request.TranslatedSegment.Text);
        if (tokens.Length == 0)
        {
            return Task.FromResult(TranslatedWordAlignmentResult.Unavailable(
                "Translated text produced no tokenizable words."));
        }

        // Step 2: determine the time budget.
        (double timeStart, double timeEnd) = ResolveTimeBudget(request);
        double totalDuration = timeEnd - timeStart;
        if (totalDuration <= 0d)
        {
            return Task.FromResult(TranslatedWordAlignmentResult.Unavailable(
                "Usable time budget for translated word alignment is zero or negative."));
        }

        // Step 3: compute per-word character counts (minimum 1 to avoid zero-weight tokens).
        int[] charCounts = Array.ConvertAll(tokens, static t => Math.Max(1, t.Length));
        int totalChars = 0;
        foreach (int c in charCounts)
        {
            totalChars += c;
        }

        // Step 4: distribute the time budget proportionally.
        // Compute start/end from cumulative character proportions anchored at timeStart/timeEnd
        // rather than accumulating a cursor, to prevent floating-point rounding from drifting
        // past timeEnd and violating downstream segment-envelope bounds checks.
        var result = new List<TranslatedWord>(tokens.Length);
        int cumulativeBefore = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            int cumulativeAfter = cumulativeBefore + charCounts[i];

            double wordStart = Math.Clamp(
                timeStart + (double)cumulativeBefore / totalChars * totalDuration,
                timeStart, timeEnd);
            double wordEnd = i == tokens.Length - 1
                ? timeEnd
                : Math.Clamp(
                    timeStart + (double)cumulativeAfter / totalChars * totalDuration,
                    timeStart, timeEnd);

            // Defensive guard: ensure end >= start regardless of floating-point edge cases.
            wordEnd = Math.Max(wordStart, wordEnd);

            result.Add(TranslatedWord.Create(i, wordStart, wordEnd, tokens[i]));
            cumulativeBefore = cumulativeAfter;
        }

        return Task.FromResult(TranslatedWordAlignmentResult.Succeeded(result));
    }

    /// <summary>
    /// Returns the [start, end] time range within which translated words are distributed.
    /// Uses the span of real ASR source-word timings when available; falls back to the
    /// translated segment's own bounds otherwise. The returned range is always clamped
    /// to the translated segment envelope so that downstream validation passes.
    /// </summary>
    private static (double Start, double End) ResolveTimeBudget(TranslatedWordAlignmentRequest request)
    {
        IReadOnlyList<TranscriptWord> sourceWords = request.SourceSegment.Words;
        TranslatedSegment translated = request.TranslatedSegment;

        if (sourceWords.Count == 0)
        {
            return (translated.StartSeconds, translated.EndSeconds);
        }

        double minStart = double.MaxValue;
        double maxEnd = double.MinValue;
        foreach (TranscriptWord word in sourceWords)
        {
            if (word.StartSeconds < minStart)
            {
                minStart = word.StartSeconds;
            }

            if (word.EndSeconds > maxEnd)
            {
                maxEnd = word.EndSeconds;
            }
        }

        // Clamp to the translated segment envelope so downstream validation always passes.
        // Source words should already be within their owning segment's bounds, but this is
        // a safety measure in case of any cross-segment timing drift.
        double start = Math.Clamp(minStart, translated.StartSeconds, translated.EndSeconds);
        double end = Math.Clamp(maxEnd, translated.StartSeconds, translated.EndSeconds);

        // If clamping collapsed the window, fall back to the full segment bounds.
        return start < end
            ? (start, end)
            : (translated.StartSeconds, translated.EndSeconds);
    }

    private static string[] TokenizeWords(string text) =>
        text.Split([' ', '\t', '\r', '\n', '\u00A0'], StringSplitOptions.RemoveEmptyEntries);
}
