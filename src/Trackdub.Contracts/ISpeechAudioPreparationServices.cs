using Trackdub.Domain.AudioQuality;

namespace Trackdub.Contracts;

public interface IAudioQualityAnalyzer
{
    Task<AudioQualityAnalysisResult> AnalyzeAsync(
        AudioQualityAnalysisRequest request,
        CancellationToken cancellationToken);
}

public interface ISpeechAudioPreparationPlanner
{
    SpeechAudioPreparationPlan Plan(SpeechAudioPreparationPlanningRequest request);
}

public interface ISpeechAudioProcessingService
{
    Task<SpeechAudioProcessingResult> ProcessAsync(
        SpeechAudioProcessingRequest request,
        CancellationToken cancellationToken);
}

public sealed record AudioQualityAnalysisRequest(
    string AudioPath,
    SpeechAudioSourceKind SourceKind,
    AudioQualityAnalysisThresholds Thresholds);

public sealed record SpeechAudioProcessingRequest(
    string SourceAudioPath,
    string DestinationPath,
    SpeechAudioFilterSelection FilterSelection);

public sealed record SpeechAudioProcessingResult(
    string OutputPath,
    double DurationSeconds,
    int SampleRate,
    int ChannelCount,
    long SampleFrames,
    SpeechAudioFilterSelection FilterSelection);

public sealed record SpeechAudioPreparationPlanningRequest(
    AudioQualityAnalysisResult FullMixAnalysis);
