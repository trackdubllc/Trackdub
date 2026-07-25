namespace Trackdub.Contracts;

public interface ITtsAudioPostProcessor
{
    Task<TtsAudioPostProcessResult> ProcessAsync(
        TtsAudioPostProcessRequest request,
        CancellationToken cancellationToken);
}

public sealed record TtsAudioPostProcessRequest(
    string AudioPath,
    int SampleRate,
    int DurationSamples);

public sealed record TtsAudioPostProcessResult(
    int DurationSamples,
    int SampleRate,
    int LeadingTrimmedSamples = 0,
    int TrailingTrimmedSamples = 0)
{
    public double? DurationSeconds =>
        SampleRate > 0 && DurationSamples >= 0
            ? (double)DurationSamples / SampleRate
            : null;
}
