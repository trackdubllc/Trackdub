using System.Collections.Concurrent;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

/// <summary>Receives each translated segment as soon as the translation stream yields it.</summary>
public interface ITranslatedSegmentObserver
{
    void OnSegmentTranslated(int segmentIndex, string text);
}

/// <summary>Everything about a rendered take except the artifact and take rows that will own it.</summary>
internal sealed record TtsRenderOutput(
    string ModelId,
    string VoiceId,
    string Provider,
    int ArtifactDurationSamples,
    int ArtifactSampleRate,
    double? ArtifactDurationSeconds,
    DurationAnalysisResult Analysis,
    double? PreStretchDurationSeconds,
    double? StretchRatioApplied,
    TtsStretchMode StretchMode,
    TtsStretchEngine StretchEngine);

/// <summary>A prefetched clip handed to the TTS stage; disposing it deletes the staged file.</summary>
internal sealed class TtsPrefetchedClip(string audioPath, TtsRenderOutput output) : IDisposable
{
    public string AudioPath { get; } = audioPath;

    public TtsRenderOutput Output { get; } = output;

    public void Dispose()
    {
        try
        {
            File.Delete(AudioPath);
        }
        catch (IOException)
        {
            // The staging directory is removed when the prefetch is disposed.
        }
    }
}

/// <summary>Stock-voice render inputs for one speaker, resolved once by the TTS orchestration.</summary>
internal sealed record TtsPrefetchSpeaker(
    string TargetLanguage,
    VoiceCatalogEntry Voice,
    InferenceRequestOptions Options,
    IReadOnlyDictionary<int, TranscriptSegment> SourceSegmentsByIndex);

/// <summary>
/// Overlaps TTS with translation. While the translation stream is still producing segments, each
/// segment is synthesized into a staging file. The TTS stage later claims clips by content key
/// (<see cref="StartTtsStageHandler.ComputeRenderKey"/>) and publishes them through the normal
/// atomic take commit, so the translation revision still becomes visible all at once and takes
/// are still validated against the committed segments.
/// </summary>
/// <remarks>
/// A clip is only an accelerator: when the committed text, voice or model selection differs from
/// what was rendered, the key does not match and the stage synthesizes normally. Voice-cloned
/// speakers are never prefetched, so consent and audit checks keep running before any cloned
/// audio is produced.
/// </remarks>
public sealed class TtsStreamingPrefetch : ITranslatedSegmentObserver, IAsyncDisposable
{
    private readonly StartTtsStageHandler handler;
    private readonly IReadOnlyDictionary<int, TtsPrefetchSpeaker> speakersBySegmentIndex;
    private readonly ConcurrentDictionary<string, Task<TtsPrefetchedClip?>> renders = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim renderSlots;
    private readonly CancellationTokenSource lifetime;
    private readonly string stagingDirectory;
    private readonly IApplicationLogger? logger;
    private int scheduled;
    private int claimed;
    private int missed;

    internal TtsStreamingPrefetch(
        StartTtsStageHandler handler,
        IReadOnlyDictionary<int, TtsPrefetchSpeaker> speakersBySegmentIndex,
        IReadOnlyDictionary<Guid, TtsSpeakerPlan> speakerPlans,
        int maxConcurrency,
        IApplicationLogger? logger,
        CancellationToken runCancellationToken)
    {
        this.handler = handler;
        this.speakersBySegmentIndex = speakersBySegmentIndex;
        SpeakerPlans = speakerPlans;
        this.logger = logger;
        renderSlots = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(runCancellationToken);
        stagingDirectory = Path.Join(Path.GetTempPath(), "trackdub-tts-prefetch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
    }

    /// <summary>Speaker voice plans resolved before translation; the TTS stage reuses them.</summary>
    internal IReadOnlyDictionary<Guid, TtsSpeakerPlan> SpeakerPlans { get; }

    internal string StagingDirectory => stagingDirectory;

    public int ScheduledCount => Volatile.Read(ref scheduled);

    public int ClaimedCount => Volatile.Read(ref claimed);

    public int MissedCount => Volatile.Read(ref missed);

    public void OnSegmentTranslated(int segmentIndex, string text)
    {
        if (lifetime.IsCancellationRequested ||
            string.IsNullOrWhiteSpace(text) ||
            !speakersBySegmentIndex.TryGetValue(segmentIndex, out TtsPrefetchSpeaker? speaker) ||
            !speaker.SourceSegmentsByIndex.TryGetValue(segmentIndex, out TranscriptSegment? sourceSegment))
        {
            return;
        }

        string key = handler.ComputeRenderKey(text, speaker.TargetLanguage, speaker.Voice, speaker.Options, sourceSegment);
        if (renders.ContainsKey(key))
        {
            return;
        }

        Interlocked.Increment(ref scheduled);
        renders.TryAdd(key, Task.Run(() => RenderAsync(key, text, speaker, sourceSegment), lifetime.Token));
    }

    internal async Task<TtsPrefetchedClip?> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        if (!renders.TryRemove(key, out Task<TtsPrefetchedClip?>? render))
        {
            Interlocked.Increment(ref missed);
            return null;
        }

        TtsPrefetchedClip? clip;
        try
        {
            clip = await render.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            clip = null;
        }

        if (clip is null)
        {
            Interlocked.Increment(ref missed);
            return null;
        }

        Interlocked.Increment(ref claimed);
        return clip;
    }

    private async Task<TtsPrefetchedClip?> RenderAsync(
        string key,
        string text,
        TtsPrefetchSpeaker speaker,
        TranscriptSegment sourceSegment)
    {
        CancellationToken token = lifetime.Token;
        await renderSlots.WaitAsync(token).ConfigureAwait(false);
        string audioPath = Path.Join(stagingDirectory, key + ".wav");
        try
        {
            TtsRenderOutput output = await handler.RenderToPathAsync(
                text,
                speaker.TargetLanguage,
                speaker.Voice,
                speaker.Options,
                cloneReference: null,
                sourceSegment,
                audioPath,
                token).ConfigureAwait(false);
            return new TtsPrefetchedClip(audioPath, output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stage will synthesize this segment itself and surface any real failure there.
            logger?.LogWarning($"TTS prefetch for segment {sourceSegment.SegmentIndex} failed; the TTS stage will synthesize it.", ex);
            return null;
        }
        finally
        {
            renderSlots.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        foreach (Task<TtsPrefetchedClip?> render in renders.Values)
        {
            try
            {
                (await render.ConfigureAwait(false))?.Dispose();
            }
            catch (OperationCanceledException)
            {
            }
        }

        renders.Clear();
        logger?.LogInformation(
            $"TTS prefetch: {ScheduledCount} segment(s) rendered during translation, {ClaimedCount} claimed by the TTS stage, {MissedCount} synthesized by the stage.");
        try
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
        catch (IOException)
        {
        }

        lifetime.Dispose();
        renderSlots.Dispose();
    }
}
