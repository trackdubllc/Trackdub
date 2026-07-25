using Trackdub.Contracts;
using Trackdub.Domain.AudioQuality;

namespace Trackdub.TestDoubles;

public sealed class FakeAudioQualityAnalyzer : IAudioQualityAnalyzer
{
    private readonly Queue<AudioQualityAnalysisResult> queuedResults = new();

    public List<AudioQualityAnalysisRequest> Requests { get; } = [];

    public AudioQualityMetrics DefaultMetrics { get; set; } = new(
        DurationSeconds: 12.0d,
        PeakDbfs: -6.0d,
        RmsDbfs: -24.0d,
        ActiveRmsDbfs: -20.0d,
        Lufs: null,
        AudioQualityAnalysisConfidence.High,
        SpeechAudioSourceKind.FullMix,
        ClippedSamplePercent: 0.0d,
        NearSilencePercent: 0.0d,
        DcOffset: 0.0d,
        RumbleRatioDb: -30.0d,
        HissRatioDb: -30.0d,
        SpeechBandRatioDb: -3.0d,
        CrestFactorDb: 18.0d,
        DynamicRangeDb: 12.0d,
        NoiseFloorDbfs: -50.0d,
        SnrDb: 30.0d,
        AudioSnrConfidence.Reliable);

    public IReadOnlyList<AudioQualityDefectKind> DefaultDefects { get; set; } = [];

    public void QueueResult(AudioQualityAnalysisResult result) => queuedResults.Enqueue(result);

    public Task<AudioQualityAnalysisResult> AnalyzeAsync(
        AudioQualityAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (queuedResults.Count > 0)
        {
            return Task.FromResult(queuedResults.Dequeue());
        }

        AudioQualityMetrics metrics = DefaultMetrics with
        {
            SourceKind = request.SourceKind
        };
        return Task.FromResult(new AudioQualityAnalysisResult(
            request.AudioPath,
            metrics,
            request.Thresholds,
            DefaultDefects,
            []));
    }
}

public sealed class FakeSpeechAudioProcessingService : ISpeechAudioProcessingService
{
    public List<SpeechAudioProcessingRequest> Requests { get; } = [];

    public double DurationSeconds { get; set; } = 12.0d;

    public int SampleRate { get; set; } = 48000;

    public int ChannelCount { get; set; } = 1;

    public long SampleFrames { get; set; } = 576000;

    public int? ThrowOnCallNumber { get; set; }

    public async Task<SpeechAudioProcessingResult> ProcessAsync(
        SpeechAudioProcessingRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (ThrowOnCallNumber == Requests.Count)
        {
            throw new InvalidOperationException("Fake speech audio processing failure.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
        await File.WriteAllBytesAsync(request.DestinationPath, FakeWavHelper.MinimalPcm16(), cancellationToken).ConfigureAwait(false);
        return new SpeechAudioProcessingResult(
            request.DestinationPath,
            DurationSeconds,
            SampleRate,
            ChannelCount,
            SampleFrames,
            request.FilterSelection);
    }
}
