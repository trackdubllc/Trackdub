using System.Text.Json;
using Trackdub.Inference.Onnx.Chatterbox;

namespace Trackdub.Inference.Onnx.Tests.Chatterbox;

public sealed class ChatterboxUnknownTokenTests
{
    [Fact]
    public void ResolveUnknownToken_uses_endoftext_for_turbo_style_tokenizer()
    {
        using JsonDocument model = JsonDocument.Parse("""{ "type": "BPE" }""");
        var vocabulary = new Dictionary<string, int> { ["a"] = 0, ["<|endoftext|>"] = 50256 };

        string? token = ChatterboxVoiceCloneTtsEngine.ResolveUnknownToken(model.RootElement, vocabulary);

        Assert.Equal("<|endoftext|>", token);
    }

    [Fact]
    public void ResolveUnknownToken_uses_declared_unk_token_for_multilingual_tokenizer()
    {
        using JsonDocument model = JsonDocument.Parse("""{ "type": "BPE", "unk_token": "[UNK]" }""");
        var vocabulary = new Dictionary<string, int> { ["[STOP]"] = 0, ["[UNK]"] = 1, ["a"] = 2 };

        string? token = ChatterboxVoiceCloneTtsEngine.ResolveUnknownToken(model.RootElement, vocabulary);

        Assert.Equal("[UNK]", token);
    }

    [Fact]
    public void ResolveUnknownToken_ignores_declared_token_missing_from_vocabulary()
    {
        using JsonDocument model = JsonDocument.Parse("""{ "type": "BPE", "unk_token": "[UNK]" }""");
        var vocabulary = new Dictionary<string, int> { ["a"] = 0, ["<|endoftext|>"] = 1 };

        string? token = ChatterboxVoiceCloneTtsEngine.ResolveUnknownToken(model.RootElement, vocabulary);

        Assert.Equal("<|endoftext|>", token);
    }

    [Fact]
    public void ResolveUnknownToken_returns_null_when_no_unknown_token_exists()
    {
        using JsonDocument model = JsonDocument.Parse("""{ "type": "BPE", "unk_token": null }""");
        var vocabulary = new Dictionary<string, int> { ["a"] = 0 };

        string? token = ChatterboxVoiceCloneTtsEngine.ResolveUnknownToken(model.RootElement, vocabulary);

        Assert.Null(token);
    }
}
