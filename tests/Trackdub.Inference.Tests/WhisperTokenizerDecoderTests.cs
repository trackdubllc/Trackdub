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

    private static string ResolveModelRootPath(string modelDirectoryName) =>
        Path.GetFullPath(Path.Combine(TestRepoRootResolver.FindRepoRoot(), "models", modelDirectoryName));
}
