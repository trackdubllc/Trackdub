using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeReferenceClipAnalyzer : IReferenceClipAnalyzer
{
    private readonly Queue<ReferenceClipAnalysis> queuedAnalyses = new();

    public ReferenceClipAnalysis Analysis { get; set; } = new(
        TotalDurationSeconds: 5d,
        ActiveSpeechSeconds: 5d,
        SampleRate: 24000,
        ChannelCount: 1);

    public int AnalyzeCallCount { get; private set; }

    public void QueueAnalysis(ReferenceClipAnalysis analysis) => queuedAnalyses.Enqueue(analysis);

    public Task<ReferenceClipAnalysis> AnalyzeAsync(string wavePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalyzeCallCount++;
        return Task.FromResult(queuedAnalyses.Count == 0 ? Analysis : queuedAnalyses.Dequeue());
    }
}
