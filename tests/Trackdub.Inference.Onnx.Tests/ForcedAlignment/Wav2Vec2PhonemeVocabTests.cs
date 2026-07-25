using System.Text.Json;
using Trackdub.Inference.Onnx.ForcedAlignment;

namespace Trackdub.Inference.Onnx.Tests.ForcedAlignment;

public sealed class Wav2Vec2PhonemeVocabTests : IDisposable
{
    private readonly string _tempFile;

    public Wav2Vec2PhonemeVocabTests()
    {
        // Small synthetic vocab that mirrors the real file format:
        // { "symbol": index, ... }  where blank is always index 0.
        var vocab = new Dictionary<string, int>
        {
            ["<pad>"] = 0,   // CTC blank at index 0
            ["|"] = 1,       // word boundary
            ["a"] = 2,
            ["b"] = 3,
            ["æ"] = 4,
        };

        _tempFile = Path.GetTempFileName();
        File.WriteAllText(_tempFile, JsonSerializer.Serialize(vocab));
    }

    public void Dispose() => File.Delete(_tempFile);

    [Fact]
    public void BlankIndex_IsAlways0()
    {
        var sut = new Wav2Vec2PhonemeVocab(_tempFile);
        Assert.Equal(0, sut.BlankIndex);
    }

    [Fact]
    public void TryGetSymbol_RoundTrip_ReturnsOriginalSymbol()
    {
        var sut = new Wav2Vec2PhonemeVocab(_tempFile);

        bool found = sut.TryGetSymbol(2, out string symbol);

        Assert.True(found);
        Assert.Equal("a", symbol);
    }

    [Fact]
    public void WordBoundarySymbol_FoundAtCorrectIndex()
    {
        var sut = new Wav2Vec2PhonemeVocab(_tempFile);

        Assert.Equal("|", sut.WordBoundarySymbol);
        Assert.Equal(1, sut.WordBoundaryIndex);

        bool found = sut.TryGetIndex("|", out int idx);
        Assert.True(found);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void TryGetSymbol_UnknownIndex_ReturnsFalse()
    {
        var sut = new Wav2Vec2PhonemeVocab(_tempFile);

        bool found = sut.TryGetSymbol(999, out string symbol);

        Assert.False(found);
        Assert.Equal(string.Empty, symbol);
    }

    [Fact]
    public void TryGetIndex_KnownSymbol_ReturnsCorrectIndex()
    {
        var sut = new Wav2Vec2PhonemeVocab(_tempFile);

        bool found = sut.TryGetIndex("æ", out int index);

        Assert.True(found);
        Assert.Equal(4, index);
    }

    [Fact]
    public void TryGetIndex_UnknownSymbol_ReturnsFalse()
    {
        var sut = new Wav2Vec2PhonemeVocab(_tempFile);

        bool found = sut.TryGetIndex("zzz", out int index);

        Assert.False(found);
        Assert.Equal(0, index); // default(int)
    }

    [Fact]
    public void Count_ReflectsMaxIndexPlusOne()
    {
        var sut = new Wav2Vec2PhonemeVocab(_tempFile);
        // Max index in our test vocab is 4, so Count should be 5
        Assert.Equal(5, sut.Count);
    }
}
