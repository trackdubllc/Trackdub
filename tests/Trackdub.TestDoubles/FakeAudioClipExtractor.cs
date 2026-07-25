using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

/// <summary>
/// Deterministic test double for <see cref="IAudioClipExtractor"/>.
/// By default it creates an empty WAV-stub file at the destination path and returns
/// a 44 100 Hz mono result. Set <see cref="ThrowOnExtract"/> to simulate failures.
/// </summary>
public sealed class FakeAudioClipExtractor : IAudioClipExtractor
{
    public bool ThrowOnExtract { get; set; }
    public int CallCount { get; private set; }
    public string? LastSourcePath { get; private set; }
    public double LastStartSeconds { get; private set; }
    public double LastEndSeconds { get; private set; }

    public Task<AudioClipExtractionResult> ExtractAsync(
        string sourceWavePath,
        double startSeconds,
        double endSeconds,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastSourcePath = sourceWavePath;
        LastStartSeconds = startSeconds;
        LastEndSeconds = endSeconds;
        CallCount++;

        if (ThrowOnExtract)
            throw new InvalidOperationException("FakeAudioClipExtractor: simulated extraction failure.");

        // Create a stub file so the handler has a file to feed the aligner.
        var dir = Path.GetDirectoryName(destinationPath);
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(destinationPath, []);

        double duration = Math.Max(0.0, endSeconds - startSeconds);
        return Task.FromResult(new AudioClipExtractionResult(
            OutputPath: destinationPath,
            DurationSeconds: duration,
            SampleRate: 44_100,
            ChannelCount: 1));
    }

    public Task<AudioClipExtractionResult> ExtractAsync(
        string sourceWavePath,
        IReadOnlyList<AudioClipRange> ranges,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;

        if (ThrowOnExtract)
            throw new InvalidOperationException("FakeAudioClipExtractor: simulated extraction failure.");

        var dir = Path.GetDirectoryName(destinationPath);
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(destinationPath, []);

        double duration = ranges.Sum(r => Math.Max(0.0, r.EndSeconds - r.StartSeconds));
        return Task.FromResult(new AudioClipExtractionResult(
            OutputPath: destinationPath,
            DurationSeconds: duration,
            SampleRate: 44_100,
            ChannelCount: 1));
    }
}
