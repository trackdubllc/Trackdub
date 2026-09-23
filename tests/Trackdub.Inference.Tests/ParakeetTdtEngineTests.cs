using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.ParakeetTdt;
using Xunit;

namespace Trackdub.Inference.Tests;

public sealed class ParakeetTdtEngineTests
{
    [Fact]
    public void ResolveClaimInterval_SplitsGapsAtMidpointAndCapsAtPadding()
    {
        SpeechRegion[] regions =
        [
            new(0, 1.0, 2.0),
            new(1, 2.4, 4.0),
            new(2, 9.0, 10.0),
        ];

        Assert.Equal((0.5, 2.2), Round(ParakeetTdtOnnxAudioTranscriptionEngine.ResolveClaimInterval(regions, 0)));
        Assert.Equal((2.2, 4.5), Round(ParakeetTdtOnnxAudioTranscriptionEngine.ResolveClaimInterval(regions, 1)));
        Assert.Equal((8.5, 10.5), Round(ParakeetTdtOnnxAudioTranscriptionEngine.ResolveClaimInterval(regions, 2)));
    }

    [Fact]
    public void ResolveClaimInterval_ClampsStartAtZero()
    {
        SpeechRegion[] regions = [new(0, 0.2, 1.0)];

        Assert.Equal(0.0, ParakeetTdtOnnxAudioTranscriptionEngine.ResolveClaimInterval(regions, 0).Start);
    }

    [Fact]
    public async Task GroupWords_JoinsPiecesAndKeepsOnlyWordsStartingInClaim()
    {
        ParakeetTdtVocab vocab = await LoadVocabAsync(["<unk>", "▁hel", "lo", "▁world", "▁x", "<blk>"]);
        ParakeetTdtGreedyDecoder.EmittedToken[] tokens =
        [
            new(4, Frame: 0, Duration: 1),  // "x" at 0.00 s, before the claim
            new(1, Frame: 10, Duration: 1), // "hel" at 0.80 s
            new(2, Frame: 11, Duration: 2), // "lo"
            new(0, Frame: 12, Duration: 0), // <unk> control token, dropped
            new(3, Frame: 20, Duration: 1), // "world" at 1.60 s, past the claim
        ];

        List<RecognizedTranscriptWord> words = ParakeetTdtOnnxAudioTranscriptionEngine.GroupWords(
            tokens, vocab, windowStart: 0.0, claimStart: 0.5, claimEnd: 1.5, firstWordIndex: 0);

        RecognizedTranscriptWord word = Assert.Single(words);
        Assert.Equal("hello", word.Text);
        Assert.Equal(0.8, word.StartSeconds, 3);
        Assert.Equal(1.04, word.EndSeconds, 3);
    }

    [Fact]
    public void DropBoundaryDuplicates_RemovesRepeatedWordFromLaterRegionOnly()
    {
        List<List<RecognizedTranscriptWord>> regions =
        [
            [Word(0, 4.0, "me"), Word(1, 4.3, "doy")],
            [Word(0, 4.4, "doy,"), Word(1, 4.7, "cuenta")],
            [Word(0, 9.0, "cuenta")],
        ];

        ParakeetTdtOnnxAudioTranscriptionEngine.DropBoundaryDuplicates(regions);

        Assert.Equal(["me", "doy"], regions[0].Select(static w => w.Text));
        Assert.Equal(["cuenta"], regions[1].Select(static w => w.Text));
        // Same text but far apart in time is a genuine repeat, not an overlap artifact.
        Assert.Equal(["cuenta"], regions[2].Select(static w => w.Text));
    }

    [Fact]
    public async Task Vocab_RejectsFileWithoutTrailingBlank()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => LoadVocabAsync(["<unk>", "▁a"]));
    }

    private static RecognizedTranscriptWord Word(int index, double start, string text) =>
        new(index, start, start + 0.2, text);

    private static (double, double) Round((double Start, double End) interval) =>
        (Math.Round(interval.Start, 3), Math.Round(interval.End, 3));

    private static async Task<ParakeetTdtVocab> LoadVocabAsync(string[] pieces)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(path, pieces.Select(static (piece, id) => $"{piece} {id}"));
            return await ParakeetTdtVocab.LoadAsync(path, CancellationToken.None);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
