using Trackdub.Contracts;

namespace Trackdub.Sdk;

public sealed record SdkSessionOptions
{
    public string? DefaultSourceLanguage { get; init; }
    public string? DefaultTargetLanguage { get; init; }
    public string ModelTierPreference { get; init; } = "balanced";
    public TtsTimingSettings? TtsTiming { get; init; }
    /// <summary>Configured maximum TTS degree of parallelism before the per-model VRAM-aware cap. Null means the historical default (4).</summary>
    public int? TtsMaxConcurrency { get; init; }
    public AsrModelOverride AsrModelOverride { get; init; } = AsrModelOverride.Auto;
}
