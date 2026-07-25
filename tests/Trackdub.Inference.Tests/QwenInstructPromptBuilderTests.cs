using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.QwenTextRefinement;

namespace Trackdub.Inference.Tests;

public sealed class QwenInstructPromptBuilderTests
{
    [Fact]
    public void BuildAsrPolishPrompt_uses_conservative_system_prompt_and_segment_text()
    {
        string prompt = QwenInstructPromptBuilder.BuildAsrPolishPrompt("hello world");

        Assert.Contains("<|im_start|>system", prompt, StringComparison.Ordinal);
        Assert.Contains("You polish automatic speech recognition transcript text.", prompt, StringComparison.Ordinal);
        Assert.Contains("When unsure, preserve the original wording.", prompt, StringComparison.Ordinal);
        Assert.Contains("<|im_start|>user", prompt, StringComparison.Ordinal);
        Assert.Contains("hello world", prompt, StringComparison.Ordinal);
        Assert.EndsWith("<|im_start|>assistant\n", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAsrPolishPrompt_includes_language_prefix_when_provided()
    {
        string prompt = QwenInstructPromptBuilder.BuildAsrPolishPrompt("bonjour", "fr");

        Assert.Contains("The transcript language is fr.", prompt, StringComparison.Ordinal);
        Assert.Contains("bonjour", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_routes_asr_scope_to_asr_template()
    {
        string prompt = QwenInstructPromptBuilder.BuildPrompt(
            TextRefinementScope.Asr,
            "test segment",
            sourceLanguage: "en",
            targetLanguage: null);

        Assert.Contains("automatic speech recognition", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test segment", prompt, StringComparison.Ordinal);
    }
}
