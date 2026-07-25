using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.DeepFilterNet;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Composition.DeepFilterNet;

public sealed class DeepFilterNetEnhancementEngine : ISpeechAudioEnhancementService
{
    private readonly DeepFilterNetModelPaths modelPaths;

    public DeepFilterNetEnhancementEngine(DeepFilterNetModelPaths modelPaths)
    {
        this.modelPaths = modelPaths ?? throw new ArgumentNullException(nameof(modelPaths));
    }

    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);

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

        float[] cleanPcm = await DeepFilterNetChunkedEnhancer
            .EnhanceAsync(resampled, sessions, cancellationToken)
            .ConfigureAwait(false);

        await WaveAudioWriter.WriteMonoPcm16Async(
            fullDestinationPath,
            cleanPcm,
            DeepFilterNetSignalProcessor.SampleRate,
            cancellationToken).ConfigureAwait(false);

        double durationSeconds = (double)cleanPcm.Length / DeepFilterNetSignalProcessor.SampleRate;

        return new SpeechAudioEnhancementResult(
            fullDestinationPath,
            durationSeconds,
            DeepFilterNetSignalProcessor.SampleRate,
            ChannelCount: 1,
            SampleFrames: cleanPcm.Length);
    }
}
