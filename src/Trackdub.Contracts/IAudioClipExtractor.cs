namespace Trackdub.Contracts;

public interface IAudioClipExtractor
{
    Task<AudioClipExtractionResult> ExtractAsync(
        string sourceWavePath,
        double startSeconds,
        double endSeconds,
        string destinationPath,
        CancellationToken cancellationToken);

    Task<AudioClipExtractionResult> ExtractAsync(
        string sourceWavePath,
        IReadOnlyList<AudioClipRange> ranges,
        string destinationPath,
        CancellationToken cancellationToken);
}

public sealed record AudioClipRange(
    double StartSeconds,
    double EndSeconds);

public sealed record AudioClipExtractionResult(
    string OutputPath,
    double DurationSeconds,
    int SampleRate,
    int ChannelCount);
