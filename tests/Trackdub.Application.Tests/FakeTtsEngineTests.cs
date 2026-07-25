using Trackdub.Contracts.Pipeline;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class FakeTtsEngineTests
{
    private static VoiceCatalogEntry TestVoice => new("af_heart", "en-us", "female", "Heart");

    [Fact]
    public async Task FakeTtsEngine_RecordsLastInputAndVoice()
    {
        var engine = new FakeTtsEngine();
        var request = new TtsSynthesisRequest("Hello world", "en-us", TestVoice);

        await engine.SynthesizeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("Hello world", engine.LastInputText);
        Assert.Equal("af_heart", engine.LastVoicepack?.VoiceId);
    }

    [Fact]
    public async Task FakeTtsEngine_ReturnsValidWavBytes()
    {
        var engine = new FakeTtsEngine();
        var request = new TtsSynthesisRequest("Test", "en-us", TestVoice);

        TtsSynthesisResult result = await engine.SynthesizeAsync(request, TestContext.Current.CancellationToken);

        // RIFF header check
        Assert.True(result.WavBytes.Length >= 44);
        Assert.Equal((byte)'R', result.WavBytes[0]);
        Assert.Equal((byte)'I', result.WavBytes[1]);
        Assert.Equal((byte)'F', result.WavBytes[2]);
        Assert.Equal((byte)'F', result.WavBytes[3]);
        Assert.Equal((byte)'W', result.WavBytes[8]);
        Assert.Equal((byte)'A', result.WavBytes[9]);
        Assert.Equal((byte)'V', result.WavBytes[10]);
        Assert.Equal((byte)'E', result.WavBytes[11]);
    }

    [Fact]
    public async Task FakeTtsEngine_ReturnsExpectedMetadata()
    {
        var engine = new FakeTtsEngine();
        var request = new TtsSynthesisRequest("Test", "en-us", TestVoice);

        TtsSynthesisResult result = await engine.SynthesizeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(24000, result.SampleRate);
        Assert.Equal(240, result.DurationSamples);
        Assert.Equal("af_heart", result.VoiceId);
        Assert.Equal("fake", result.Provider);
    }

    [Fact]
    public void FakeVoiceCatalog_GetVoices_ReturnsAllByDefault()
    {
        var catalog = new FakeVoiceCatalog();

        IReadOnlyList<VoiceCatalogEntry> voices = catalog.GetVoices();

        Assert.Equal(3, voices.Count);
    }

    [Fact]
    public void FakeVoiceCatalog_GetVoices_FiltersByLanguage()
    {
        var catalog = new FakeVoiceCatalog([
            new VoiceCatalogEntry("bf_alice", "en-gb", "female", "Alice (British English)"),
            new VoiceCatalogEntry("af_heart", "en-us", "female", "Heart (American English)")
        ]);

        IReadOnlyList<VoiceCatalogEntry> voices = catalog.GetVoices("en-gb");

        Assert.Single(voices);
        Assert.Equal("bf_alice", voices[0].VoiceId);
    }

    [Fact]
    public void FakeVoiceCatalog_TryGetVoice_ReturnsTrueForKnownVoice()
    {
        var catalog = new FakeVoiceCatalog();

        bool found = catalog.TryGetVoice("am_adam", out VoiceCatalogEntry? entry);

        Assert.True(found);
        Assert.NotNull(entry);
        Assert.Equal("male", entry.Gender);
    }

    [Fact]
    public void FakePhonemizer_ReturnsFixedPhonemes()
    {
        var phonemizer = new FakePhonemizer("t@st");

        string result = phonemizer.Phonemize("anything", "en-us");

        Assert.Equal("t@st", result);
    }

    [Fact]
    public async Task FakeTtsEngine_NullRequest_ThrowsArgumentNullException()
    {
        var engine = new FakeTtsEngine();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => engine.SynthesizeAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FakeTtsEngine_SecondCall_UpdatesLastInputText()
    {
        var engine = new FakeTtsEngine();
        var firstRequest = new TtsSynthesisRequest("First", "en-us", TestVoice);
        var secondRequest = new TtsSynthesisRequest("Second", "en-us", TestVoice);

        await engine.SynthesizeAsync(firstRequest, TestContext.Current.CancellationToken);
        await engine.SynthesizeAsync(secondRequest, TestContext.Current.CancellationToken);

        Assert.Equal("Second", engine.LastInputText);
    }

    [Fact]
    public async Task FakeTtsEngine_WavBytesLengthConsistentWithMetadata()
    {
        var engine = new FakeTtsEngine();
        var request = new TtsSynthesisRequest("Test", "en-us", TestVoice);

        TtsSynthesisResult result = await engine.SynthesizeAsync(request, TestContext.Current.CancellationToken);

        // WAV data section = DurationSamples * 2 (16-bit mono) + 44 header
        int expectedLength = 44 + result.DurationSamples * 2;
        Assert.Equal(expectedLength, result.WavBytes.Length);
    }

    [Fact]
    public async Task FakeTtsEngine_ModelIdIsFake()
    {
        var engine = new FakeTtsEngine();
        var request = new TtsSynthesisRequest("Test", "en-us", TestVoice);

        TtsSynthesisResult result = await engine.SynthesizeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("fake", result.ModelId);
    }

    [Fact]
    public void FakeVoiceCatalog_TryGetVoice_ReturnsFalseForUnknownVoice()
    {
        var catalog = new FakeVoiceCatalog();

        bool found = catalog.TryGetVoice("nonexistent_voice", out VoiceCatalogEntry? entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    [Fact]
    public void FakeVoiceCatalog_WithCustomVoices_ReturnsCustomVoices()
    {
        var customVoices = new List<VoiceCatalogEntry>
        {
            new("custom_voice", "en-us", "male", "Custom")
        };
        var catalog = new FakeVoiceCatalog(customVoices);

        IReadOnlyList<VoiceCatalogEntry> voices = catalog.GetVoices();

        Assert.Single(voices);
        Assert.Equal("custom_voice", voices[0].VoiceId);
    }

    [Fact]
    public void FakeVoiceCatalog_GetVoices_ReturnsEmpty_WhenNoMatchingLanguage()
    {
        var catalog = new FakeVoiceCatalog();

        IReadOnlyList<VoiceCatalogEntry> voices = catalog.GetVoices("zh");

        Assert.Empty(voices);
    }

    [Fact]
    public void FakePhonemizer_DefaultPhonemes_ReturnExpectedDefault()
    {
        var phonemizer = new FakePhonemizer();

        string result = phonemizer.Phonemize("anything", "en-us");

        Assert.Equal("h@l@U", result);
    }

    [Fact]
    public void FakePhonemizer_IgnoresInputText_AlwaysReturnsFixed()
    {
        var phonemizer = new FakePhonemizer("fixed");

        Assert.Equal("fixed", phonemizer.Phonemize("hello", "en-us"));
        Assert.Equal("fixed", phonemizer.Phonemize("goodbye", "fr"));
        Assert.Equal("fixed", phonemizer.Phonemize("", "de"));
    }
}
