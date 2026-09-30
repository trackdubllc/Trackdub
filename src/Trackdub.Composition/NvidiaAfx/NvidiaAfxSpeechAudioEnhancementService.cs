using Trackdub.Contracts;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Media.Waveforms;

namespace Trackdub.Composition.NvidiaAfx;

public sealed class NvidiaAfxSpeechAudioEnhancementService(
    INvidiaAfxRuntimeReadinessService readinessService,
    ISpeechAudioEnhancementService ffmpegFallback) : ISpeechAudioEnhancementService
{
    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        SpeechAudioEnhancementOptions options = request.Options ?? SpeechAudioEnhancementOptions.Default;
        NvidiaAfxRuntimeReadiness readiness = readinessService.GetReadiness(options.NvidiaAfxProfile);
        // Stubbed integration and unreadiness both fall through to the primary backend
        // (DeepFilterNet via ResolvingSpeechAudioEnhancementService). Never pretend AFX ran.
        if (!options.EnableNvidiaAfx || NvidiaAfxIntegration.IsStubbed() || !readiness.IsReady)
        {
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }

        NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(options.NvidiaAfxProfile);
        if (definition.RequiresFarEndReference && string.IsNullOrWhiteSpace(options.FarEndReferenceAudioPath))
        {
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using IAudioSamples source = await WaveAudioReader
                .ReadMonoPcm16Async(request.SourceAudioPath, cancellationToken)
                .ConfigureAwait(false);

            int targetSampleRate = definition.SupportedSampleRates.Contains(source.SampleRate)
                ? source.SampleRate
                : definition.SupportedSampleRates[0];

            float[] monoSamples;
            if (source.SampleRate == targetSampleRate)
            {
                monoSamples = ReadAllSamples(source);
            }
            else
            {
                using IAudioSamples resampled = AudioResampler.CreateResampledStream(source, targetSampleRate);
                monoSamples = ReadAllSamples(resampled);
            }

            float[]? farEndSamples = null;
            if (definition.RequiresFarEndReference)
            {
                farEndSamples = await ReadMonoSamplesAtRateAsync(
                    options.FarEndReferenceAudioPath!,
                    targetSampleRate,
                    cancellationToken).ConfigureAwait(false);
            }

            using NvidiaAfxSession session = NvidiaAfxSession.Create(
                definition,
                readiness.RuntimeRoot!,
                targetSampleRate,
                options.NvidiaAfxIntensityRatio);
            float[] enhanced = session.Process(monoSamples, farEndSamples);

            int outputSampleRate = targetSampleRate;
            await WaveAudioWriter.WriteMonoPcm16Async(
                request.DestinationPath,
                enhanced,
                outputSampleRate,
                cancellationToken).ConfigureAwait(false);

            return new SpeechAudioEnhancementResult(
                request.DestinationPath,
                DurationSeconds: (double)enhanced.Length / outputSampleRate,
                SampleRate: outputSampleRate,
                ChannelCount: 1,
                SampleFrames: enhanced.Length,
                Backend: SpeechAudioEnhancementBackend.NvidiaAfx,
                BackendProfile: definition.Selector);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<float[]> ReadMonoSamplesAtRateAsync(
        string path,
        int targetSampleRate,
        CancellationToken cancellationToken)
    {
        using IAudioSamples source = await WaveAudioReader
            .ReadMonoPcm16Async(path, cancellationToken)
            .ConfigureAwait(false);
        if (source.SampleRate == targetSampleRate)
        {
            return ReadAllSamples(source);
        }

        using IAudioSamples resampled = AudioResampler.CreateResampledStream(source, targetSampleRate);
        return ReadAllSamples(resampled);
    }

    private static float[] ReadAllSamples(IAudioSamples audio)
    {
        var samples = new float[audio.SampleFrameCount];
        audio.ReadMonoSamples(0L, samples.AsSpan());
        return samples;
    }
}
