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
        if (!options.EnableNvidiaAfx || !readiness.IsReady)
        {
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }

        NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(options.NvidiaAfxProfile);

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

            using NvidiaAfxSession session = NvidiaAfxSession.Create(
                definition,
                readiness.RuntimeRoot!,
                targetSampleRate,
                channels: 1,
                options.NvidiaAfxIntensityRatio);
            float[] enhanced = session.Process(monoSamples);

            await WaveAudioWriter.WriteMonoPcm16Async(
                request.DestinationPath,
                enhanced,
                targetSampleRate,
                cancellationToken).ConfigureAwait(false);

            return new SpeechAudioEnhancementResult(
                request.DestinationPath,
                DurationSeconds: (double)enhanced.Length / targetSampleRate,
                SampleRate: targetSampleRate,
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

    private static float[] ReadAllSamples(IAudioSamples audio)
    {
        var samples = new float[audio.SampleFrameCount];
        audio.ReadMonoSamples(0L, samples.AsSpan());
        return samples;
    }
}
