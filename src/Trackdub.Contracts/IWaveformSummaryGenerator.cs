namespace Trackdub.Contracts;

public interface IWaveformSummaryGenerator
{
    Task<WaveformSummary> GenerateAsync(string audioPath, CancellationToken cancellationToken);
}

public sealed record WaveformSummary(
    int BucketCount,
    int SampleRate,
    int ChannelCount,
    double DurationSeconds,
    IReadOnlyList<float> Peaks);
