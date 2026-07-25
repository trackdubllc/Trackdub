using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Pipelines.Transcript;

public sealed class ScriptedSpeechRegionDetector : ISpeechRegionDetector
{
    public Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        string normalizedAudioPath,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return Task.FromResult<IReadOnlyList<SpeechRegion>>(Array.Empty<SpeechRegion>());
        }

        int segmentCount = Math.Clamp((int)Math.Ceiling(durationSeconds / 8d), 1, 6);
        double gapSeconds = durationSeconds > 4d ? 0.20d : 0.05d;
        double rawSegmentDuration = durationSeconds / segmentCount;
        var regions = new List<SpeechRegion>(segmentCount);
        double cursor = 0d;

        for (int index = 0; index < segmentCount; index++)
        {
            double start = cursor;
            double end = index == segmentCount - 1
                ? durationSeconds
                : Math.Min(durationSeconds, start + Math.Max(0.60d, rawSegmentDuration - gapSeconds));

            if (end <= start)
            {
                end = Math.Min(durationSeconds, start + 0.60d);
            }

            regions.Add(new SpeechRegion(index, start, end));
            cursor = Math.Min(durationSeconds, end + gapSeconds);
        }

        return Task.FromResult<IReadOnlyList<SpeechRegion>>(regions);
    }
}
