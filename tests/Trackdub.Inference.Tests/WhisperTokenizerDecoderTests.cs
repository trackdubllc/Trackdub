using Trackdub.Inference.Onnx.Whisper;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

public sealed class WhisperTokenizerDecoderTests
{
    [RequiresBundledModelFact("whisper-tiny-onnx/vocab.json", "whisper-tiny-onnx/config.json")]
    public async Task BuildTranscriptionPrompt_DoesNotReuseForcedEnglishToken()
    {
        string modelRootPath = ResolveModelRootPath("whisper-tiny-onnx");
        var tokenizer = await WhisperTokenizerDecoder.LoadAsync(modelRootPath);

        IReadOnlyList<int> prompt = tokenizer.BuildTranscriptionPrompt(languageTokenId: 50262);

        Assert.Equal([50258, 50262, 50359, 50363], prompt);
        Assert.DoesNotContain(50259, prompt);
        Assert.Equal("en", tokenizer.TryGetLanguageCode(50259));
        Assert.Equal("es", tokenizer.TryGetLanguageCode(50262));
        Assert.Null(tokenizer.TryGetLanguageCode(50359));
    }

    [Fact]
    public async Task LoadAsync_ResolvesTimestampBeginTokenFromAddedTokensDecoder()
    {
        string tempDir = Path.Join(Path.GetTempPath(), "whisper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Join(tempDir, "vocab.json"), "{\"hello\": 0, \"world\": 1}");
            await File.WriteAllTextAsync(Path.Join(tempDir, "config.json"), "{\"decoder_start_token_id\": 50258, \"eos_token_id\": 50257, \"suppress_tokens\": [], \"begin_suppress_tokens\": []}");
            await File.WriteAllTextAsync(Path.Join(tempDir, "tokenizer_config.json"), "{\"added_tokens_decoder\": {\"50365\": {\"content\": \"<|0.00|>\"}}}");

            var tokenizer = await WhisperTokenizerDecoder.LoadAsync(tempDir);

            Assert.Equal(50365, tokenizer.TimestampBeginToken);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string ResolveModelRootPath(string modelDirectoryName) =>
        Path.GetFullPath(Path.Join(TestRepoRootResolver.FindRepoRoot(), "models", modelDirectoryName));
}
