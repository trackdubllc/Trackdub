namespace Trackdub.Contracts;

public interface IReferenceClipAnalyzer
{
    Task<ReferenceClipAnalysis> AnalyzeAsync(string wavePath, CancellationToken cancellationToken);
}

public sealed record ReferenceClipAnalysis(
    double TotalDurationSeconds,
    double ActiveSpeechSeconds,
    int SampleRate,
    int ChannelCount)
{
    public bool HasRecommendedMaximumWarning =>
        ActiveSpeechSeconds > ReferenceClipPolicy.RecommendedMaximumActiveSpeechSeconds;
}

public static class ReferenceClipPolicy
{
    public const double MinimumActiveSpeechSeconds = 3.0d;
    public const double RecommendedMaximumActiveSpeechSeconds = 10.0d;
}
