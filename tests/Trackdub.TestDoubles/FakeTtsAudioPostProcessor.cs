using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeTtsAudioPostProcessor(
    int? durationSamples = null,
    int leadingTrimmedSamples = 0,
    int trailingTrimmedSamples = 0) : ITtsAudioPostProcessor
{
    public TtsAudioPostProcessRequest? LastRequest { get; private set; }

    public Task<TtsAudioPostProcessResult> ProcessAsync(
        TtsAudioPostProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        LastRequest = request;
        int samples = durationSamples ?? request.DurationSamples;
        return Task.FromResult(new TtsAudioPostProcessResult(
            samples,
            request.SampleRate,
            leadingTrimmedSamples,
            trailingTrimmedSamples));
    }
}
