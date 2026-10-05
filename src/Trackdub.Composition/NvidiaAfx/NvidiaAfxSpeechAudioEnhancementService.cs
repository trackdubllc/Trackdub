using Trackdub.Contracts;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Media.Waveforms;

namespace Trackdub.Composition.NvidiaAfx;

public sealed class NvidiaAfxSpeechAudioEnhancementService(
    INvidiaAfxRuntimeReadinessService readinessService,
    ISpeechAudioEnhancementService ffmpegFallback) : ISpeechAudioEnhancementService
{
    /// <summary>
    /// Test seam for exercising the kill switch and the gates behind it (AEC far-end, readiness)
    /// without changing <see cref="NvidiaAfxIntegration.IsStubbed"/> for the whole process.
    /// </summary>
    internal Func<bool>? IsStubbedOverride { get; set; }

    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        SpeechAudioEnhancementOptions options = request.Options ?? SpeechAudioEnhancementOptions.Default;
        // Gate before readiness: TryAddSingleton hosts may replace readiness with a probe that
        // throws. Disabled/stubbed AFX must still fall through to DeepFilterNet safely.
        // Never pretend AFX ran.
        if (!options.EnableNvidiaAfx || IsIntegrationStubbed())
        {
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }

        NvidiaAfxRuntimeReadiness readiness = readinessService.GetReadiness(options.NvidiaAfxProfile);
        if (!readiness.IsReady)
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
                options.NvidiaAfxIntensityRatio,
                readiness.ArchitectureBucket);
            float[] enhanced = session.Process(monoSamples, farEndSamples);

            // Prefer the native/session output rate (telephony upscale is 8 kHz in → 16 kHz out).
            int outputSampleRate = session.OutputSampleRate > 0
                ? session.OutputSampleRate
                : definition.ResolveOutputSampleRate(targetSampleRate);
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

    private bool IsIntegrationStubbed() =>
        IsStubbedOverride?.Invoke() ?? NvidiaAfxIntegration.IsStubbed();

    private static float[] ReadAllSamples(IAudioSamples audio)
    {
        var samples = new float[audio.SampleFrameCount];
        audio.ReadMonoSamples(0L, samples.AsSpan());
        return samples;
    }
}
