using Trackdub.Contracts;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Media.Waveforms;

namespace Trackdub.Composition.NvidiaAfx;

public sealed class NvidiaAfxSpeechAudioEnhancementService(
    INvidiaAfxRuntimeReadinessService readinessService,
    ISpeechAudioEnhancementService ffmpegFallback,
    Func<bool>? isStubbed = null,
    IApplicationLogger? logger = null) : ISpeechAudioEnhancementService
{
    private readonly Func<bool> _isStubbed = isStubbed ?? NvidiaAfxIntegration.IsStubbed;

    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        SpeechAudioEnhancementOptions options = request.Options ?? SpeechAudioEnhancementOptions.Default;
        // Gate before readiness: TryAddSingleton hosts may replace readiness with a probe that
        // throws. Disabled/stubbed AFX must still fall through to DeepFilterNet safely.
        // Never pretend AFX ran.
        if (!options.EnableNvidiaAfx || _isStubbed())
        {
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }

        NvidiaAfxRuntimeReadiness readiness;
        try
        {
            readiness = readinessService.GetReadiness(options.NvidiaAfxProfile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A readiness probe that throws (settings I/O, native load) must not fail the stage.
            logger?.LogWarning(
                $"NVIDIA AFX readiness check threw for profile '{options.NvidiaAfxProfile}'; falling back to DeepFilterNet.",
                ex);
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (!readiness.IsReady)
        {
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }

        NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(options.NvidiaAfxProfile);
        if (definition.RequiresFarEndReference && string.IsNullOrWhiteSpace(options.FarEndReferenceAudioPath))
        {
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }

        string fullDestinationPath = Path.GetFullPath(request.DestinationPath);
        // One partial file per attempt, so concurrent calls for the same destination never write or
        // delete each other's file.
        string partialPath = $"{fullDestinationPath}.{Guid.NewGuid():N}.partial";
        try
        {
            using IAudioSamples source = await WaveAudioReader
                .ReadMonoPcm16Async(request.SourceAudioPath, cancellationToken)
                .ConfigureAwait(false);

            int targetSampleRate = definition.SupportedSampleRates.Contains(source.SampleRate)
                ? source.SampleRate
                : definition.SupportedSampleRates[0];

            using IAudioSamples? nearResampled = ResampleOrNull(source, targetSampleRate);
            IAudioSamples nearEnd = nearResampled ?? source;

            using IAudioSamples? farEndSource = definition.RequiresFarEndReference
                ? await WaveAudioReader
                    .ReadMonoPcm16Async(options.FarEndReferenceAudioPath!, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            using IAudioSamples? farResampled = farEndSource is null
                ? null
                : ResampleOrNull(farEndSource, targetSampleRate);
            IAudioSamples? farEnd = farResampled ?? farEndSource;

            using NvidiaAfxSession session = NvidiaAfxSession.Create(
                definition,
                readiness.RuntimeRoot!,
                targetSampleRate,
                options.NvidiaAfxIntensityRatio,
                readiness.ArchitectureBucket);

            // Prefer the native/session output rate (telephony upscale is 8 kHz in → 16 kHz out).
            int outputSampleRate = session.OutputSampleRate > 0
                ? session.OutputSampleRate
                : definition.ResolveOutputSampleRate(targetSampleRate);

            Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationPath)!);
            long sampleFrames;
            await using (var destination = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 8192, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var writer = await StreamingMonoPcm16WaveWriter.CreateAsync(
                    destination,
                    session.GetOutputSampleCount(nearEnd.SampleFrameCount),
                    outputSampleRate,
                    cancellationToken).ConfigureAwait(false);
                sampleFrames = await session
                    .ProcessStreamAsync(nearEnd, farEnd, writer.WriteAsync, cancellationToken)
                    .ConfigureAwait(false);
                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partialPath, fullDestinationPath, overwrite: true);

            return new SpeechAudioEnhancementResult(
                request.DestinationPath,
                DurationSeconds: (double)sampleFrames / outputSampleRate,
                SampleRate: outputSampleRate,
                ChannelCount: 1,
                SampleFrames: sampleFrames,
                Backend: SpeechAudioEnhancementBackend.NvidiaAfx,
                BackendProfile: definition.Selector);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Native create/run and file failures all land here; keep the reason so a fallback caused
            // by a failed AFX attempt is distinguishable from an intentional one.
            logger?.LogWarning(
                $"NVIDIA AFX enhancement failed for profile '{definition.Selector}'; falling back to DeepFilterNet.",
                ex);
            return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private static IAudioSamples? ResampleOrNull(IAudioSamples source, int targetSampleRate) =>
        source.SampleRate == targetSampleRate
            ? null
            : AudioResampler.CreateResampledStream(source, targetSampleRate);
}
