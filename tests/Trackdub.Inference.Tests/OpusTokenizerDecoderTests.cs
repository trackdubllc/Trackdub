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

    private static string ResolveModelRootPath(string modelDirectoryName) =>
        Path.GetFullPath(Path.Combine(TestRepoRootResolver.FindRepoRoot(), "models", "opus", modelDirectoryName));
}
