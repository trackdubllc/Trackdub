using Trackdub.Contracts;

namespace Trackdub.Sdk;

public sealed record SdkSessionOptions
{
    public string? DefaultSourceLanguage { get; init; }
    public string? DefaultTargetLanguage { get; init; }
    public string ModelTierPreference { get; init; } = "balanced";
    public TtsTimingSettings? TtsTiming { get; init; }
    public AsrModelOverride AsrModelOverride { get; init; } = AsrModelOverride.Auto;
}
