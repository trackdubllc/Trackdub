using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeSpeechRegionDetector : ISpeechRegionDetector, IStageRuntimeExecutionReporter
{
    private IReadOnlyList<SpeechRegion> regions =
    [
        new SpeechRegion(0, 0.0d, 5.8d),
        new SpeechRegion(1, 6.0d, 11.8d)
    ];

    private Exception? exceptionToThrow;

    public string? LastNormalizedAudioPath { get; private set; }

    public double? LastDurationSeconds { get; private set; }

    public int DetectCallCount { get; private set; }

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public void SetRegions(params SpeechRegion[] speechRegions)
    {
        ArgumentNullException.ThrowIfNull(speechRegions);
        regions = speechRegions;
    }

    public void SetException(Exception exception) => exceptionToThrow = exception;

    public Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        string normalizedAudioPath,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAudioPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (exceptionToThrow is not null)
        {
            throw exceptionToThrow;
        }

        LastNormalizedAudioPath = normalizedAudioPath;
        LastDurationSeconds = durationSeconds;
        DetectCallCount++;
        LastExecutionSummary = new StageRuntimeExecutionSummary(
            "auto",
            "cpu",
            "fake/vad",
            "fake-vad",
            "default",
            "Fake speech region detector");

        return Task.FromResult(regions);
    }
}
