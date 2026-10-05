using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.DeepFilterNet;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Composition.DeepFilterNet;

public sealed class DeepFilterNetEnhancementEngine : ISpeechAudioEnhancementService
{
    // Upstream enhance(atten_lim_db): cap noise suppression so some of the source stays in.
    // Unlimited suppression smeared speech enough to cost ASR words on real clips; 6 dB kept
    // transcripts on par with the unprocessed audio.
    public const double DefaultAttenuationLimitDb = 6;

    private readonly DeepFilterNetModelPaths modelPaths;
    private readonly double attenuationLimitDb;

    public DeepFilterNetEnhancementEngine(
        DeepFilterNetModelPaths modelPaths,
        double attenuationLimitDb = DefaultAttenuationLimitDb)
    {
        this.modelPaths = modelPaths ?? throw new ArgumentNullException(nameof(modelPaths));
        ArgumentOutOfRangeException.ThrowIfNegative(attenuationLimitDb);
        this.attenuationLimitDb = attenuationLimitDb;
    }

    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!modelPaths.AllFilesExist())
        {
            throw new RequiredModelNotAvailableException(
                "Rikorose/DeepFilterNet3",
                modelPaths.RootDirectory);
        }

        string fullSourcePath = Path.GetFullPath(request.SourceAudioPath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("Source speech audio file was not found.", fullSourcePath);
        }

        string fullDestinationPath = Path.GetFullPath(request.DestinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationPath)!);
        if (File.Exists(fullDestinationPath))
        {
            File.Delete(fullDestinationPath);
        }

        using IAudioSamples audio = await WaveAudioReader
            .ReadMonoPcm16Async(fullSourcePath, cancellationToken)
            .ConfigureAwait(false);
        using IAudioSamples resampled = AudioResampler.CreateResampledStream(
            audio, DeepFilterNetSignalProcessor.SampleRate);

        using DeepFilterNetModelSessions sessions = await DeepFilterNetModelSessions
            .CreateAsync(modelPaths, ExecutionProviderKind.Cpu, cancellationToken)
            .ConfigureAwait(false);

        string partialPath = fullDestinationPath + ".partial";
        long sampleFrames;
        try
        {
            await using (var destination = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 8192, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var writer = await StreamingMonoPcm16WaveWriter.CreateAsync(
                    destination,
                    DeepFilterNetStreamingEnhancer.GetOutputSampleCount(resampled.SampleFrameCount),
                    DeepFilterNetSignalProcessor.SampleRate,
                    cancellationToken).ConfigureAwait(false);
                sampleFrames = await DeepFilterNetStreamingEnhancer
                    .EnhanceAsync(resampled, sessions, ToLinearLimit(attenuationLimitDb),
                        writer.WriteAsync, cancellationToken)
                    .ConfigureAwait(false);
                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partialPath, fullDestinationPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            throw;
        }

        double durationSeconds = (double)sampleFrames / DeepFilterNetSignalProcessor.SampleRate;

        return new SpeechAudioEnhancementResult(
            fullDestinationPath,
            durationSeconds,
            DeepFilterNetSignalProcessor.SampleRate,
            ChannelCount: 1,
            SampleFrames: sampleFrames,
            Backend: SpeechAudioEnhancementBackend.DeepFilterNet,
            BackendProfile: attenuationLimitDb > 0
                ? FormattableString.Invariant($"atten-lim-{attenuationLimitDb:0.#}db")
                : "unlimited");
    }

    internal static float ToLinearLimit(double attenuationLimitDb) =>
        attenuationLimitDb > 0 ? (float)Math.Pow(10, -attenuationLimitDb / 20) : 0f;
}
