using Trackdub.Contracts;
using Trackdub.Domain.Tts;

namespace Trackdub.TestDoubles;

public sealed class FakeAudioTimeStretchService : IAudioTimeStretchService
{
    public List<double> StretchRatios { get; } = [];

    public AudioTimeStretchRequest? LastRequest { get; private set; }

    public bool RubberbandAvailable { get; set; } = true;

    public Task<AudioTimeStretchResult> StretchAsync(
        AudioTimeStretchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        LastRequest = request;
        StretchRatios.Add(request.TempoRatio);
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        File.Copy(request.InputPath, request.OutputPath, overwrite: true);

        bool wantsRubberband = request.EnableRubberband &&
                               Math.Abs(request.TempoRatio - 1d) >= request.RubberbandThreshold;
        TtsStretchEngine engine = wantsRubberband && RubberbandAvailable
            ? TtsStretchEngine.Rubberband
            : TtsStretchEngine.Atempo;
        bool usedFallback = wantsRubberband && !RubberbandAvailable;
        return Task.FromResult(new AudioTimeStretchResult(
            engine,
            usedFallback,
            usedFallback ? "FFmpeg rubberband filter is unavailable; used atempo instead." : null));
    }
}
