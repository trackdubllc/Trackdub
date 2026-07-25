namespace Trackdub.Contracts.Pipeline;

public interface IAudioSegmentExtractor
{
    /// <summary>
    /// Extracts a time-bounded segment from an audio file into a PCM WAV at 16 kHz mono.
    /// Returns the path to the output WAV file.
    /// </summary>
    Task<string> ExtractSegmentAsync(
        string sourceAudioPath,
        TimeSpan start,
        TimeSpan end,
        string outputWavPath,
        CancellationToken cancellationToken);
}
