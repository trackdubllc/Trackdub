using Trackdub.Application.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Tests;

public sealed class ProportionalTranslatedWordAlignmentServiceTests
{
    private static readonly ProportionalTranslatedWordAlignmentService Sut = new();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TranslatedWordAlignmentRequest MakeRequest(
        string translatedText,
        double segStart,
        double segEnd,
        IReadOnlyList<TranscriptWord>? sourceWords = null)
    {
        Guid transcriptRevisionId = Guid.NewGuid();
        Guid translationRevisionId = Guid.NewGuid();

        TranscriptSegment source = TranscriptSegment.Create(
            transcriptRevisionId,
            segmentIndex: 0,
            startSeconds: segStart,
            endSeconds: segEnd,
            text: "source text",
            words: sourceWords);

        TranslatedSegment translated = TranslatedSegment.Create(
            translationRevisionId,
            segmentIndex: 0,
            startSeconds: segStart,
            endSeconds: segEnd,
            text: translatedText);

        return new TranslatedWordAlignmentRequest(
            source,
            translated,
            SourceLanguage: "en",
            TargetLanguage: "es");
    }

    private static TranscriptWord Word(int index, double start, double end, string text) =>
        TranscriptWord.Create(index, start, end, text);

    // ── No source words: fall back to segment bounds ─────────────────────────

    [Fact]
    public async Task AlignAsync_no_source_words_returns_succeeded()
    {
        TranslatedWordAlignmentRequest request = MakeRequest("hola mundo", 0.0, 2.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(TranslatedWordAlignmentOutcomeKind.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task AlignAsync_no_source_words_produces_two_words()
    {
        TranslatedWordAlignmentRequest request = MakeRequest("hola mundo", 0.0, 2.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Words.Count);
        Assert.Equal("hola", result.Words[0].Text);
        Assert.Equal("mundo", result.Words[1].Text);
    }

    [Fact]
    public async Task AlignAsync_no_source_words_first_word_starts_at_segment_start()
    {
        TranslatedWordAlignmentRequest request = MakeRequest("hola mundo", 1.0, 3.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, result.Words[0].StartSeconds, precision: 6);
    }

    [Fact]
    public async Task AlignAsync_no_source_words_last_word_ends_at_segment_end()
    {
        TranslatedWordAlignmentRequest request = MakeRequest("hola mundo", 1.0, 3.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(3.0, result.Words[^1].EndSeconds, precision: 6);
    }

    [Fact]
    public async Task AlignAsync_no_source_words_words_are_non_overlapping()
    {
        TranslatedWordAlignmentRequest request = MakeRequest("hola mundo amigo", 0.0, 3.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        for (int i = 1; i < result.Words.Count; i++)
        {
            Assert.Equal(result.Words[i - 1].EndSeconds, result.Words[i].StartSeconds, precision: 9);
        }
    }

    [Fact]
    public async Task AlignAsync_no_source_words_proportional_by_character_count()
    {
        // "ab" = 2 chars, "cdef" = 4 chars → ratio 1:2, segment [0, 3]
        // "ab"   → [0.0, 1.0]
        // "cdef" → [1.0, 3.0]
        TranslatedWordAlignmentRequest request = MakeRequest("ab cdef", 0.0, 3.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, result.Words[0].EndSeconds, precision: 6);
        Assert.Equal(1.0, result.Words[1].StartSeconds, precision: 6);
        Assert.Equal(3.0, result.Words[1].EndSeconds, precision: 6);
    }

    // ── With source words: uses ASR span as budget ───────────────────────────

    [Fact]
    public async Task AlignAsync_with_source_words_uses_source_span_as_budget()
    {
        // Segment [0, 4], source words span [1.0, 3.0] → budget is [1.0, 3.0]
        var sourceWords = new[]
        {
            Word(0, 1.0, 2.0, "Hello"),
            Word(1, 2.0, 3.0, "world")
        };
        TranslatedWordAlignmentRequest request = MakeRequest("Hola mundo", 0.0, 4.0, sourceWords);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(TranslatedWordAlignmentOutcomeKind.Succeeded, result.Outcome);
        Assert.Equal(1.0, result.Words[0].StartSeconds, precision: 6);
        Assert.Equal(3.0, result.Words[^1].EndSeconds, precision: 6);
    }

    [Fact]
    public async Task AlignAsync_with_source_words_result_within_segment_bounds()
    {
        var sourceWords = new[]
        {
            Word(0, 0.5, 1.5, "Hello"),
            Word(1, 1.5, 2.0, "there")
        };
        TranslatedWordAlignmentRequest request = MakeRequest("Hola mundo", 0.0, 2.0, sourceWords);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.All(result.Words, word =>
        {
            Assert.True(word.StartSeconds >= 0.0);
            Assert.True(word.EndSeconds <= 2.0);
        });
    }

    [Fact]
    public async Task AlignAsync_with_source_words_words_are_non_overlapping()
    {
        var sourceWords = new[]
        {
            Word(0, 0.2, 1.0, "Hello"),
            Word(1, 1.0, 1.8, "world")
        };
        TranslatedWordAlignmentRequest request = MakeRequest("Hola bonito mundo", 0.0, 2.0, sourceWords);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        for (int i = 1; i < result.Words.Count; i++)
        {
            Assert.Equal(result.Words[i - 1].EndSeconds, result.Words[i].StartSeconds, precision: 9);
        }
    }

    // ── Single-word translated text ───────────────────────────────────────────

    [Fact]
    public async Task AlignAsync_single_word_covers_full_budget()
    {
        TranslatedWordAlignmentRequest request = MakeRequest("Hola", 1.0, 3.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(TranslatedWordAlignmentOutcomeKind.Succeeded, result.Outcome);
        Assert.Single(result.Words);
        Assert.Equal(1.0, result.Words[0].StartSeconds, precision: 6);
        Assert.Equal(3.0, result.Words[0].EndSeconds, precision: 6);
        Assert.Equal("Hola", result.Words[0].Text);
    }

    // ── Word indices are sequential ───────────────────────────────────────────

    [Fact]
    public async Task AlignAsync_word_indices_are_sequential_from_zero()
    {
        TranslatedWordAlignmentRequest request = MakeRequest("uno dos tres cuatro", 0.0, 4.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        for (int i = 0; i < result.Words.Count; i++)
        {
            Assert.Equal(i, result.Words[i].WordIndex);
        }
    }

    // ── Passes HasRenderableTranslatedWordAlignment validation ────────────────

    [Fact]
    public async Task AlignAsync_output_passes_orchestration_text_mapping_check()
    {
        // The orchestration service rejects words whose text can't be found in order
        // inside the segment text. Verify the service always produces mappable words.
        TranslatedWordAlignmentRequest request = MakeRequest("segunda linea de prueba", 2.0, 5.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(TranslatedWordAlignmentOutcomeKind.Succeeded, result.Outcome);

        // Replay CanMapWordsToTranslatedText logic.
        int searchStart = 0;
        foreach (TranslatedWord word in result.Words)
        {
            int idx = request.TranslatedSegment.Text.IndexOf(
                word.Text, searchStart, StringComparison.OrdinalIgnoreCase);
            Assert.True(idx >= 0, $"Word '{word.Text}' not found at or after position {searchStart}.");
            searchStart = idx + word.Text.Length;
        }
    }

    // ── Whitespace edge cases ─────────────────────────────────────────────────

    [Fact]
    public async Task AlignAsync_extra_whitespace_is_ignored()
    {
        TranslatedWordAlignmentRequest request = MakeRequest("hola   mundo", 0.0, 2.0);

        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Words.Count);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AlignAsync_already_cancelled_token_still_returns_result()
    {
        // The service is synchronous internally; cancellation is checked by callers.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        TranslatedWordAlignmentRequest request = MakeRequest("hola mundo", 0.0, 2.0);

        // Should not throw — the service doesn't do async I/O so cancellation is ignored.
        TranslatedWordAlignmentResult result = await Sut.AlignAsync(request, cts.Token);

        Assert.Equal(TranslatedWordAlignmentOutcomeKind.Succeeded, result.Outcome);
    }
}
