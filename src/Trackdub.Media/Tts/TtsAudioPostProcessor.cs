using Trackdub.Contracts;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Tts;

public sealed class TtsAudioPostProcessor(IApplicationLogger? logger = null) : ITtsAudioPostProcessor
{
    private const float NearSilenceThreshold = 0.0015f;

    public async Task<TtsAudioPostProcessResult> ProcessAsync(
        TtsAudioPostProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        WaveMonoSamples mono;
        try
        {
            mono = await WavePcm16.ReadAllMonoSamplesAsync(request.AudioPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning($"TTS audio post-processing skipped because '{request.AudioPath}' could not be read.", ex);
            return CreateUnchangedResult(request);
        }

        if (mono.SampleRate <= 0 || mono.Samples.Length == 0)
        {
            return CreateUnchangedResult(request);
        }

        (int leadingTrim, int trailingTrim) = FindNearSilenceTrim(mono.Samples, mono.SampleRate);
        if (leadingTrim == 0 && trailingTrim == 0)
        {
            return new TtsAudioPostProcessResult(mono.Samples.Length, mono.SampleRate);
        }

        int trimmedLength = Math.Max(0, mono.Samples.Length - leadingTrim - trailingTrim);
        if (trimmedLength <= 0)
        {
            return new TtsAudioPostProcessResult(mono.Samples.Length, mono.SampleRate);
        }

        var trimmed = new float[trimmedLength];
        Array.Copy(mono.Samples, leadingTrim, trimmed, 0, trimmedLength);
        await WavePcm16.WriteMonoAsync(request.AudioPath, trimmed, mono.SampleRate, cancellationToken)
            .ConfigureAwait(false);
        return new TtsAudioPostProcessResult(
            trimmedLength,
            mono.SampleRate,
            leadingTrim,
            trailingTrim);
    }

    private static (int LeadingTrim, int TrailingTrim) FindNearSilenceTrim(
        IReadOnlyList<float> samples,
        int sampleRate)
    {
        int firstActive = -1;
        for (int index = 0; index < samples.Count; index++)
        {
            if (Math.Abs(samples[index]) > NearSilenceThreshold)
            {
                firstActive = index;
                break;
            }
        }

        if (firstActive < 0)
        {
            return (0, 0);
        }

        int lastActive = firstActive;
        for (int index = samples.Count - 1; index > firstActive; index--)
        {
            if (Math.Abs(samples[index]) > NearSilenceThreshold)
            {
                lastActive = index;
                break;
            }
        }

        int preserveSamples = Math.Max(1, sampleRate / 200);
        int minimumTrimSamples = Math.Max(1, sampleRate / 100);
        int leadingTrim = Math.Max(0, firstActive - preserveSamples);
        int trailingTrim = Math.Max(0, samples.Count - lastActive - 1 - preserveSamples);

        if (leadingTrim < minimumTrimSamples)
        {
            leadingTrim = 0;
        }

        if (trailingTrim < minimumTrimSamples)
        {
            trailingTrim = 0;
        }

        return (leadingTrim, trailingTrim);
    }

    private static TtsAudioPostProcessResult CreateUnchangedResult(TtsAudioPostProcessRequest request) =>
        new(request.DurationSamples, request.SampleRate);
}
