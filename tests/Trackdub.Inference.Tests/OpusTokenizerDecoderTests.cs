using Trackdub.Inference.Onnx.OpusMt;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

public sealed class OpusTokenizerDecoderTests
{
    [RequiresBundledModelFact("opus/onnx-community-opus-mt-es-en")]
    public async Task EncodeSourceText_MapsSentencePiecePiecesThroughMarianVocabulary()
    {
        string modelRootPath = ResolveModelRootPath("onnx-community-opus-mt-es-en");
        var tokenizer = await OpusTokenizerDecoder.LoadAsync(modelRootPath);

        long[] ids = tokenizer.EncodeSourceText(
            "Hola, soy Brenna Romaniello, tu profesora de español de Ole Spanish.");

        Assert.Equal(
        [
            2119L, 2L, 1434L, 5578L, 22211L, 3203L, 6942L, 5316L, 2L,
            213L, 27926L, 4L, 4522L, 4L, 425L, 380L, 2036L, 3L, 0L
        ], ids);
    }

    [Fact]
    public async Task LoadConfigAsync_MissingConfigFiles_FallsBackToVocabDerivedIds()
    {
        // opus-mt-en-fr (vocab 59514) ships no config.json in the model cache; the legacy
        // hardcoded 65000 default caused an opaque ONNX Gather OOB on the first decoder step.
        string modelRootPath = CreateIsolatedModelRoot();

        OpusTokenizerDecoder.OpusTokenizerConfig config = await OpusTokenizerDecoder.LoadConfigAsync(
            Path.Join(modelRootPath, "config.json"),
            Path.Join(modelRootPath, "generation_config.json"),
            vocabularySize: 59514);

        Assert.Equal(59513, config.DecoderStartTokenId);
        Assert.Equal(59513, config.PadTokenId);
        Assert.Equal(0, config.EndOfSentenceTokenId);
        Assert.False(config.ConfigFilePresent);
        Assert.False(config.GenerationConfigFilePresent);
    }

    [Fact]
    public async Task LoadConfigAsync_PresentConfig_WinsOverFallback()
    {
        string modelRootPath = CreateIsolatedModelRoot();
        await File.WriteAllTextAsync(
            Path.Join(modelRootPath, "config.json"),
            "{\"decoder_start_token_id\": 100, \"eos_token_id\": 2, \"pad_token_id\": 100}");

        OpusTokenizerDecoder.OpusTokenizerConfig config = await OpusTokenizerDecoder.LoadConfigAsync(
            Path.Join(modelRootPath, "config.json"),
            Path.Join(modelRootPath, "generation_config.json"),
            vocabularySize: 59514);

        Assert.Equal(100, config.DecoderStartTokenId);
        Assert.Equal(2, config.EndOfSentenceTokenId);
        Assert.Equal(100, config.PadTokenId);
        Assert.True(config.ConfigFilePresent);
        Assert.False(config.GenerationConfigFilePresent);
    }

    [Fact]
    public async Task LoadConfigAsync_PartialConfig_MissingKeysFallBackToVocab()
    {
        string modelRootPath = CreateIsolatedModelRoot();
        await File.WriteAllTextAsync(
            Path.Join(modelRootPath, "config.json"),
            "{\"eos_token_id\": 2}");

        OpusTokenizerDecoder.OpusTokenizerConfig config = await OpusTokenizerDecoder.LoadConfigAsync(
            Path.Join(modelRootPath, "config.json"),
            Path.Join(modelRootPath, "generation_config.json"),
            vocabularySize: 59514);

        Assert.Equal(59513, config.DecoderStartTokenId);
        Assert.Equal(2, config.EndOfSentenceTokenId);
        Assert.Equal(59513, config.PadTokenId);
        Assert.True(config.ConfigFilePresent);
    }

    [Fact]
    public async Task LoadConfigAsync_OutOfRangeId_ThrowsClearError()
    {
        string modelRootPath = CreateIsolatedModelRoot();
        await File.WriteAllTextAsync(
            Path.Join(modelRootPath, "config.json"),
            "{\"decoder_start_token_id\": 65000, \"eos_token_id\": 0, \"pad_token_id\": 65000}");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpusTokenizerDecoder.LoadConfigAsync(
                Path.Join(modelRootPath, "config.json"),
                Path.Join(modelRootPath, "generation_config.json"),
                vocabularySize: 59514));

        Assert.Contains("59514", exception.Message);
    }

    private static string ResolveModelRootPath(string modelDirectoryName) =>
        Path.GetFullPath(Path.Join(TestRepoRootResolver.FindRepoRoot(), "models", "opus", modelDirectoryName));

    private static string CreateIsolatedModelRoot()
    {
        string modelRootPath = Path.Join(Path.GetTempPath(), "opus-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modelRootPath);
        return modelRootPath;
    }
}
