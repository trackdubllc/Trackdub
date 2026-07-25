using Trackdub.Application.Transcripts;
using Trackdub.Domain;

namespace Trackdub.Application.Tests;

public sealed class PipelineRuntimeProvenanceFormatterTests
{
    [Fact]
    public void FormatProviderLabel_normalizes_short_and_enum_labels()
    {
        Assert.Equal("DirectML", PipelineRuntimeProvenanceFormatter.FormatProviderLabel("dml"));
        Assert.Equal("DirectML", PipelineRuntimeProvenanceFormatter.FormatProviderLabel("DirectMl"));
        Assert.Equal("TensorRT RTX", PipelineRuntimeProvenanceFormatter.FormatProviderLabel("tensorrt-rtx"));
    }

    [Fact]
    public void FormatTtsSegmentLogLine_includes_provider_model_variant_and_voice()
    {
        string line = PipelineRuntimeProvenanceFormatter.FormatTtsSegmentLogLine(
            3,
            "dml",
            "qwen3-tts-0.6b-customvoice",
            "qwen3-tts-0.6b-customvoice",
            "fp16",
            "Ryan");

        Assert.Contains("segment 3", line, StringComparison.Ordinal);
        Assert.Contains("provider=DirectML", line, StringComparison.Ordinal);
        Assert.Contains("model=qwen3-tts-0.6b-customvoice", line, StringComparison.Ordinal);
        Assert.Contains("variant=fp16", line, StringComparison.Ordinal);
        Assert.Contains("voice=Ryan", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStageRunLine_prefers_alias_and_variant()
    {
        string line = PipelineRuntimeProvenanceFormatter.FormatStageRunLine(
            new StageRunRuntimeInfo("auto", "dml", "whisper-large-v3", "whisper-large-v3", "fp16"));

        Assert.Equal("DirectML · fp16 · whisper-large-v3", line);
    }

    [Fact]
    public void FormatCollapsedSegmentBadge_includes_dub_translation_and_asr_prefixes()
    {
        string badge = PipelineRuntimeProvenanceFormatter.FormatCollapsedSegmentBadge(
            "DirectML",
            "fp16",
            "DirectML",
            "CPU");

        Assert.Equal("dub DirectML·fp16 · tr DirectML · asr CPU", badge);
    }
}
