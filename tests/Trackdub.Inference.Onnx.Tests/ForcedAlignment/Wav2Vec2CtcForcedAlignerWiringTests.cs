using System.Text.Json;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.ForcedAlignment;

namespace Trackdub.Inference.Onnx.Tests.ForcedAlignment;

public sealed class Wav2Vec2CtcForcedAlignerWiringTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _vocabPath;

    public Wav2Vec2CtcForcedAlignerWiringTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"w2v2-wiring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "onnx"));

        var vocab = new Dictionary<string, int>
        {
            ["<pad>"] = 0,
            ["|"] = 1,
            ["k"] = 2,
            ["æ"] = 3,
            ["t"] = 4,
            ["h"] = 5,
            ["ə"] = 6,
            ["l"] = 7,
            ["oʊ"] = 8,
        };

        _vocabPath = Path.Combine(_tempRoot, "vocab.json");
        File.WriteAllText(_vocabPath, JsonSerializer.Serialize(vocab));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void ResolveOnnxPath_PrefersInt8_WhenBothPresent()
    {
        string int8 = Path.Combine(_tempRoot, "onnx", "model_int8.onnx");
        string fp16 = Path.Combine(_tempRoot, "onnx", "model_fp16.onnx");
        File.WriteAllText(int8, "int8");
        File.WriteAllText(fp16, "fp16");

        string resolved = Wav2Vec2CtcForcedAligner.ResolveOnnxPath(_tempRoot);
        Assert.Equal(int8, resolved);
    }

    [Fact]
    public void ResolveOnnxPath_AcceptsFp16_WhenInt8Missing()
    {
        string fp16 = Path.Combine(_tempRoot, "onnx", "model_fp16.onnx");
        File.WriteAllText(fp16, "fp16");

        string resolved = Wav2Vec2CtcForcedAligner.ResolveOnnxPath(_tempRoot);
        Assert.Equal(fp16, resolved);

        using var aligner = new Wav2Vec2CtcForcedAligner(_tempRoot);
        Assert.True(aligner.IsAvailable);
    }

    [Fact]
    public void IsAvailable_False_WhenOnlyVocabPresent()
    {
        using var aligner = new Wav2Vec2CtcForcedAligner(_tempRoot);
        Assert.False(aligner.IsAvailable);
    }

    [Fact]
    public void IsAvailable_FlipsTrue_AfterFp16DownloadPostConstruction()
    {
        using var aligner = new Wav2Vec2CtcForcedAligner(_tempRoot);
        Assert.False(aligner.IsAvailable);

        string fp16 = Path.Combine(_tempRoot, "onnx", "model_fp16.onnx");
        File.WriteAllText(fp16, "fp16");

        Assert.True(aligner.IsAvailable);
    }

    [Fact]
    public void TryBuildPhonemeSequence_UsesPhonemizer_WhenLanguageProvided()
    {
        var vocab = new Wav2Vec2PhonemeVocab(_vocabPath);
        var phonemizer = new StubPhonemizer("h ə l oʊ");

        bool ok = Wav2Vec2CtcForcedAligner.TryBuildPhonemeSequence(
            "hello",
            vocab,
            phonemizer,
            "en",
            out int[] sequence,
            out string[]? symbols,
            out string[]? words,
            out int[]? wordMap);

        Assert.True(ok);
        Assert.Equal(4, sequence.Length);
        Assert.NotNull(symbols);
        Assert.Equal(["h", "ə", "l", "oʊ"], symbols);
        Assert.NotNull(words);
        Assert.Equal(["hello"], words);
        Assert.NotNull(wordMap);
        Assert.All(wordMap, idx => Assert.Equal(0, idx));
        Assert.Equal(1, phonemizer.CallCount);
    }

    [Fact]
    public void TryBuildPhonemeSequence_FallsBackToGraphemeMap_WithoutLanguage()
    {
        var vocab = new Wav2Vec2PhonemeVocab(_vocabPath);

        bool ok = Wav2Vec2CtcForcedAligner.TryBuildPhonemeSequence(
            "cat",
            vocab,
            phonemizer: null,
            languageCode: null,
            out int[] sequence,
            out string[]? symbols,
            out _,
            out _);

        Assert.True(ok);
        Assert.NotNull(symbols);
        Assert.Equal(["k", "æ", "t"], symbols);
        Assert.Equal(3, sequence.Length);
    }

    [Fact]
    public void TryBuildPhonemeSequence_SkipsDigitOnlyWords_WithoutAbortingTranscript()
    {
        var vocab = new Wav2Vec2PhonemeVocab(_vocabPath);

        bool ok = Wav2Vec2CtcForcedAligner.TryBuildPhonemeSequence(
            "cat 2026",
            vocab,
            phonemizer: null,
            languageCode: null,
            out int[] sequence,
            out string[]? symbols,
            out string[]? words,
            out _);

        Assert.True(ok);
        Assert.NotNull(words);
        Assert.Equal(["cat", "2026"], words);
        Assert.NotNull(symbols);
        Assert.Equal(["k", "æ", "t", "|"], symbols);
        Assert.Equal(4, sequence.Length);
    }

    [Fact]
    public void TokenizeIpaAgainstVocab_LongestMatch_PrefersDigraph()
    {
        var vocab = new Wav2Vec2PhonemeVocab(_vocabPath);
        string[] tokens = [.. Wav2Vec2CtcForcedAligner.TokenizeIpaAgainstVocab("hoʊ", vocab)];
        Assert.Equal(["h", "oʊ"], tokens);
    }

    private sealed class StubPhonemizer(string fixedIpa) : IGraphemeToPhoneme
    {
        public int CallCount { get; private set; }

        public string Phonemize(string text, string languageCode)
        {
            CallCount++;
            return fixedIpa;
        }
    }
}
