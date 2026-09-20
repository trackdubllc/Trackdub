namespace Trackdub.Application.Transcripts;

internal static class TranscriptPipelineConstants
{
    internal const double ShortAudioFallbackMaximumSeconds = 30d;

    /// <summary>
    /// Shipping ASR alias used when the selected engine returns zero segments.
    /// Matches StageRuntimeRequirements preferred aliases (first working ONNX lane).
    /// </summary>
    internal const string FallbackAsrModelAlias = "qwen3-asr-0.6b";
}
