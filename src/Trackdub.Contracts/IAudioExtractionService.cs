namespace Trackdub.Contracts;

public interface IAudioExtractionService
{
    Task<AudioExtractionResult> ExtractNormalizedAudioAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken,
        int? maxEncoderThreads = null);

    Task<AudioExtractionResult> ExtractStemSeparationAudioAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken);
}

public sealed record AudioExtractionResult(
    string OutputPath,
    double DurationSeconds,
    int SampleRate,
    int ChannelCount,
    long SampleFrames);
