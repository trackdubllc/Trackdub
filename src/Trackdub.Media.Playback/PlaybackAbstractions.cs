using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Domain.Media;

namespace Trackdub.Media.Playback;

public enum PlaybackBackendKind
{
    MediaFoundation = 0,
    FfmpegFallback = 1,
    LibMpvFallback = 2,
    LibVlc = 3,
    LibMpv = 4
}

public sealed record MediaSourceDescriptor(
    string SourcePath,
    MediaProbeSnapshot Probe);

public sealed record PlaybackSnapshot(
    bool IsLoaded,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    double PlaybackRate,
    string? WarningMessage)
{
    public static PlaybackSnapshot Empty { get; } = new(
        IsLoaded: false,
        IsPlaying: false,
        Position: TimeSpan.Zero,
        Duration: TimeSpan.Zero,
        PlaybackRate: 1d,
        WarningMessage: null);
}

public sealed record PlaybackCapabilityAssessment(
    PlaybackBackendKind PreferredBackend,
    bool IsLikelySupportedByCurrentWindowsMediaStack,
    string ContainerName,
    string? VideoCodec,
    string? AudioCodec,
    int SubtitleTrackCount,
    bool IsHdrLikely,
    string? WarningMessage);

public class PlaybackCapabilityProbe
{
    private static readonly HashSet<string> MediaFoundationContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4",
        "mov",
        "m4v",
        "m4a",
        "mp3",
        "wav",
        "aac",
        "flac"
    };

    private static readonly HashSet<string> MediaFoundationVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264",
        "avc1",
        "hevc",
        "h265",
        "av1",
        "vp9"
    };

    private static readonly HashSet<string> MediaFoundationAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac",
        "mp3",
        "ac3",
        "eac3",
        "flac",
        "alac",
        "pcm_s16le",
        "pcm_s24le",
        "pcm_f32le"
    };

    public virtual PlaybackCapabilityAssessment Assess(MediaSourceDescriptor source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.SourcePath);
        ArgumentNullException.ThrowIfNull(source.Probe);

        MediaVideoStream? primaryVideo = source.Probe.VideoStreams.FirstOrDefault();
        MediaAudioStream? primaryAudio = source.Probe.AudioStreams.FirstOrDefault();
        string containerName = SelectPrimaryContainer(source.Probe.FormatName);
        string? videoCodec = Normalize(primaryVideo?.CodecName);
        string? audioCodec = Normalize(primaryAudio?.CodecName);
        int subtitleTrackCount = source.Probe.SubtitleStreams?.Count ?? 0;
        bool isHdrLikely = primaryVideo is not null &&
                           IsHdrTransfer(primaryVideo.ColorTransfer);

        bool hasSupportedContainer = MediaFoundationContainers.Contains(containerName);
        bool hasSupportedVideo = primaryVideo is null || MediaFoundationVideoCodecs.Contains(videoCodec ?? string.Empty);
        bool hasSupportedAudio = primaryAudio is null || MediaFoundationAudioCodecs.Contains(audioCodec ?? string.Empty);
        bool prefersMediaFoundation = hasSupportedContainer && hasSupportedVideo && hasSupportedAudio;

        string? warning = prefersMediaFoundation
            ? BuildSoftWarning(subtitleTrackCount, isHdrLikely)
            : BuildFallbackWarning(containerName, videoCodec, audioCodec, subtitleTrackCount, isHdrLikely);

        return new PlaybackCapabilityAssessment(
            prefersMediaFoundation ? PlaybackBackendKind.MediaFoundation : PlaybackBackendKind.FfmpegFallback,
            prefersMediaFoundation,
            containerName,
            videoCodec,
            audioCodec,
            subtitleTrackCount,
            isHdrLikely,
            warning);
    }

    private static string SelectPrimaryContainer(string formatName)
    {
        string firstContainer = (formatName ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "unknown";
        return Normalize(firstContainer) ?? "unknown";
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();

    private static bool IsHdrTransfer(string? colorTransfer)
    {
        string? normalized = Normalize(colorTransfer);
        return normalized is "smpte2084" or "arib-std-b67";
    }

    private static string? BuildSoftWarning(int subtitleTrackCount, bool isHdrLikely)
    {
        List<string> warnings = [];
        if (subtitleTrackCount > 0)
        {
            warnings.Add($"{subtitleTrackCount} embedded subtitle track(s) detected.");
        }

        if (isHdrLikely)
        {
            warnings.Add("HDR metadata detected.");
        }

        return warnings.Count == 0 ? null : string.Join(' ', warnings);
    }

    private static string BuildFallbackWarning(
        string containerName,
        string? videoCodec,
        string? audioCodec,
        int subtitleTrackCount,
        bool isHdrLikely)
    {
        List<string> reasons =
        [
            $"Windows native playback is unlikely to support this source cleanly ({containerName}, video={videoCodec ?? "none"}, audio={audioCodec ?? "none"})."
        ];

        if (subtitleTrackCount > 0)
        {
            reasons.Add($"{subtitleTrackCount} embedded subtitle track(s) detected.");
        }

        if (isHdrLikely)
        {
            reasons.Add("HDR metadata detected.");
        }

        return string.Join(' ', reasons);
    }
}

public interface IPlaybackBackend
{
    Task OpenAsync(MediaSourceDescriptor source, CancellationToken ct);

    Task PlayAsync(CancellationToken ct);

    Task PauseAsync(CancellationToken ct);

    Task SeekAsync(TimeSpan position, CancellationToken ct);

    Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken ct);
}

public interface IPlaybackHostAwareBackend
{
    bool TryAttachHost(object host);
}

public sealed record VideoFrameDescriptor(
    int Width,
    int Height,
    int Stride,
    string PixelFormat);

public sealed record VideoFrame(
    VideoFrameDescriptor Format,
    byte[] Data);

public interface IPlaybackFrameSink
{
    void OnVideoFormatChanged(VideoFrameDescriptor format);

    void OnVideoFrameArrived(VideoFrame frame);

    void OnVideoSurfaceCleared();
}

public interface IPlaybackFrameSinkAwareBackend
{
    bool TryAttachFrameSink(IPlaybackFrameSink sink);
}

/// <summary>
/// A frame sink that can lend a compositor backend direct write access to its presentation
/// back buffer, so a software renderer (e.g. libmpv) can render straight into the locked
/// destination bitmap with no intermediate managed copy. Sinks that decode into their own
/// buffer (e.g. LibVLC) do not implement this; those keep using <see cref="IPlaybackFrameSink"/>.
/// </summary>
public interface IPlaybackDirectRenderTarget
{
    /// <summary>
    /// Acquires a write lock over the current back buffer. Called from the backend's render-loop
    /// thread, not the UI thread. Disposing the returned lock releases the buffer and presents the
    /// frame. When the back buffer is not ready (or a present is still in flight) the returned lock
    /// has a zero <see cref="DirectRenderLock.Buffer"/> and the caller must skip the frame.
    /// </summary>
    DirectRenderLock AcquireRenderLock();
}

/// <summary>
/// A borrowed lock over a presenter back buffer for zero-copy rendering. <see cref="Buffer"/> is the
/// native address the backend renders into; disposing invokes the presenter's present callback,
/// which releases the bitmap lock and swaps it onto the UI thread. A default (zero) instance is a
/// no-op on dispose, signalling the back buffer was not available.
/// </summary>
public struct DirectRenderLock : IDisposable
{
    public IntPtr Buffer { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int Stride { get; init; }

    private Action? present;

    // Public (not internal) because the presenter that constructs this lives in a different
    // assembly (Trackdub.App.Avalonia) from this type (Trackdub.Media.Playback).
    public DirectRenderLock(IntPtr buffer, int width, int height, int stride, Action present)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        this.present = present;
    }

    public void Dispose()
    {
        present?.Invoke();
        present = null;
    }
}

/// <summary>
/// Compositor backends that can render a paused preview frame before playback starts.
/// </summary>
public interface IPlaybackPreviewFrameBackend
{
    Task PreparePreviewFrameAsync(CancellationToken ct);
}

public interface IPlaybackRateBackend
{
    Task SetPlaybackRateAsync(double playbackRate, CancellationToken ct);
}

public interface IPlaybackVolumeBackend
{
    Task SetVolumeAsync(double volume, CancellationToken ct);
}

public interface IPlaybackBackendFactory
{
    IPlaybackBackend? Create(PlaybackBackendKind backendKind);
}

public sealed record PlaybackOpenResult(
    PlaybackCapabilityAssessment Assessment,
    bool IsBackendAvailable,
    PlaybackSnapshot Snapshot);

/// <summary>
/// Coarse lifecycle state of the active backend. <see cref="Opening"/> spans the slow,
/// gate-free phase of <see cref="PlaybackService.OpenAsync"/> — callers that only need to
/// know "is a backend usable right now" (the position timer, transport commands) can check
/// this without waiting on <see cref="PlaybackService"/>'s gate.
/// </summary>
public enum BackendState
{
    None,
    Opening,
    Ready,
    Failed
}

public sealed class PlaybackService(
    PlaybackCapabilityProbe capabilityProbe,
    IPlaybackBackendFactory backendFactory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IPlaybackBackend? backend;
    private IPlaybackFrameSink? pendingFrameSink;
    private long openGeneration;

    /// <summary>
    /// Volatile: read from the position timer and transport commands without acquiring
    /// <see cref="gate"/>. A stale read (seeing <see cref="BackendState.Opening"/> for one
    /// extra timer tick after it flips to <see cref="BackendState.Ready"/>) is harmless — it
    /// just costs one extra <see cref="PlaybackSnapshot.Empty"/> return.
    /// </summary>
    private volatile BackendState state = BackendState.None;

    public BackendState State => state;

    public PlaybackCapabilityAssessment? CurrentAssessment { get; private set; }

    /// <summary>
    /// Gets the currently active playback backend, or null if no backend is loaded.
    /// Shell-specific code can inspect this when it needs backend capabilities beyond the
    /// transport abstraction, but compositor-backed rendering should prefer frame sinks.
    /// </summary>
    public async Task<IPlaybackBackend?> GetCurrentBackendAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return backend;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> TryAttachHostAsync(object host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return backend is IPlaybackHostAwareBackend hostAwareBackend &&
                   hostAwareBackend.TryAttachHost(host);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> TryAttachFrameSinkAsync(IPlaybackFrameSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            pendingFrameSink = sink;
            return AttachPendingFrameSinkToCurrentBackend();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PreparePreviewFrameAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (backend is IPlaybackPreviewFrameBackend previewBackend)
            {
                await previewBackend.PreparePreviewFrameAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlaybackOpenResult> OpenAsync(MediaSourceDescriptor source, CancellationToken ct)
    {
        // Phase 1 (fast, gated): assess capability, swap in the new backend instance, mark
        // Opening. Everything that touches the shared `backend`/`CurrentAssessment` fields
        // happens under the gate; the slow phase below never does. `myGeneration` lets Phase 3
        // detect it was superseded by a later OpenAsync before it commits any result.
        PlaybackCapabilityAssessment assessment;
        IPlaybackBackend? primary;
        long myGeneration;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            assessment = capabilityProbe.Assess(source);
            CurrentAssessment = assessment;
            myGeneration = Interlocked.Increment(ref openGeneration);
            state = BackendState.Opening;

            primary = backendFactory.Create(assessment.PreferredBackend);
            ReplaceBackend(primary);
            if (primary is null)
            {
                state = BackendState.Failed;
                return new PlaybackOpenResult(
                    CurrentAssessment,
                    IsBackendAvailable: false,
                    PlaybackSnapshot.Empty with
                    {
                        WarningMessage = BuildBackendUnavailableWarning(CurrentAssessment)
                    });
            }
        }
        finally
        {
            gate.Release();
        }

        // Phase 2 (slow, gate-free): the actual libmpv/LibVLC open — this is what used to hold
        // the gate for up to ~15s and starve GetSnapshotAsync/transport calls. Only ever touches
        // the local `primary`/`vlcBackend` instances, never the shared `backend` field.
        string? primaryOpenError = null;
        try
        {
            await primary.OpenAsync(source, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            primaryOpenError = ex.Message;
        }

        PlaybackSnapshot snapshot = await primary.GetSnapshotAsync(ct).ConfigureAwait(false);

        // When OpenAsync threw, the backend state is unreliable: treat the source as
        // not loaded and surface the exception message as the playback warning so the
        // fallback / unavailability logic below can treat it uniformly.
        if (primaryOpenError is not null)
        {
            snapshot = snapshot with
            {
                IsLoaded = false,
                WarningMessage = string.IsNullOrWhiteSpace(snapshot.WarningMessage)
                    ? primaryOpenError
                    : snapshot.WarningMessage
            };
        }

        PlaybackBackendKind activeKind = assessment.PreferredBackend;
        IPlaybackBackend? vlcBackend = null;
        PlaybackCapabilityAssessment finalAssessment = assessment;

        if (assessment.PreferredBackend == PlaybackBackendKind.LibMpv &&
            !snapshot.IsLoaded &&
            backendFactory.Create(PlaybackBackendKind.LibVlc) is { } fallbackBackend)
        {
            vlcBackend = fallbackBackend;
            string? mergedProbeWarnings = MergeProbeAndRuntimeWarnings(assessment.WarningMessage, snapshot.WarningMessage);
            finalAssessment = assessment with
            {
                PreferredBackend = PlaybackBackendKind.LibVlc,
                WarningMessage = AppendSentence(
                    mergedProbeWarnings,
                    "LibVLC fallback after libmpv failed to open.")
            };

            activeKind = PlaybackBackendKind.LibVlc;
            bool vlcOpenThrew = false;
            try
            {
                await vlcBackend.OpenAsync(source, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                snapshot = snapshot with { IsLoaded = false, WarningMessage = ex.Message };
                vlcOpenThrew = true;
            }

            if (!vlcOpenThrew)
            {
                snapshot = await vlcBackend.GetSnapshotAsync(ct).ConfigureAwait(false);
            }
        }

        // Phase 3 (fast, gated): commit the result. If a newer OpenAsync has since started
        // (myGeneration is stale), this call was superseded during its gate-free phase — its
        // backend instance(s) are either already disposed (Phase 1 of the newer call replaced
        // `backend` out from under us) or were never attached; dispose them here (Dispose is
        // idempotent) and return without touching shared state.
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref openGeneration) != myGeneration)
            {
                // Dispose each backend independently so one throw cannot skip the other.
                DisposeIndependently(primary as IDisposable, vlcBackend as IDisposable);
                return new PlaybackOpenResult(
                    finalAssessment,
                    IsBackendAvailable: false,
                    PlaybackSnapshot.Empty with { WarningMessage = "Superseded by a newer selection." });
            }

            CurrentAssessment = finalAssessment;
            if (vlcBackend is not null)
            {
                ReplaceBackend(vlcBackend);
            }

            bool requiresLoadedSnapshot = activeKind is PlaybackBackendKind.LibMpv or PlaybackBackendKind.LibVlc;
            bool isBackendAvailable = backend is not null &&
                                      (!requiresLoadedSnapshot || snapshot.IsLoaded);

            state = isBackendAvailable ? BackendState.Ready : BackendState.Failed;
            return new PlaybackOpenResult(CurrentAssessment, isBackendAvailable, snapshot);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Best-effort dispose of zero or more resources. Each is attempted even if a prior dispose throws.
    /// </summary>
    internal static void DisposeIndependently(params IDisposable?[] disposables)
    {
        Exception? firstFailure = null;
        foreach (IDisposable? disposable in disposables)
        {
            if (disposable is null)
                continue;

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                // Intentionally catch all exceptions from Dispose() to ensure
                // we attempt to dispose all objects even if some fail.
                // First failure is aggregated and re-thrown after all disposals attempted.
                firstFailure ??= ex;
            }
        }

        if (firstFailure is not null)
            throw firstFailure;
    }

    private static string? MergeProbeAndRuntimeWarnings(string? probeWarning, string? runtimeWarning)
    {
        if (string.IsNullOrWhiteSpace(probeWarning))
        {
            return string.IsNullOrWhiteSpace(runtimeWarning) ? null : runtimeWarning.Trim();
        }

        if (string.IsNullOrWhiteSpace(runtimeWarning))
        {
            return probeWarning.Trim();
        }

        return $"{probeWarning.Trim()} {runtimeWarning.Trim()}";
    }

    private static string? AppendSentence(string? existing, string sentence)
    {
        sentence = sentence.Trim();
        if (string.IsNullOrWhiteSpace(existing))
        {
            return sentence;
        }

        return $"{existing.Trim()} {sentence}";
    }

    public Task PlayAsync(CancellationToken ct) =>
        RunWithBackendAsync(playbackBackend => playbackBackend.PlayAsync(ct), ct);

    public Task PauseAsync(CancellationToken ct) =>
        RunWithBackendAsync(playbackBackend => playbackBackend.PauseAsync(ct), ct);

    public Task SeekAsync(TimeSpan position, CancellationToken ct) =>
        RunWithBackendAsync(playbackBackend => playbackBackend.SeekAsync(position, ct), ct);

    public Task SetPlaybackRateAsync(double playbackRate, CancellationToken ct)
    {
        return RunWithBackendAsync(
            playbackBackend => playbackBackend is IPlaybackRateBackend rateBackend
                ? rateBackend.SetPlaybackRateAsync(playbackRate, ct)
                : Task.CompletedTask,
            ct);
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct)
    {
        double normalizedVolume = double.IsFinite(volume) ? Math.Clamp(volume, 0d, 1d) : 1d;
        return RunWithBackendAsync(
            playbackBackend => playbackBackend is IPlaybackVolumeBackend volumeBackend
                ? volumeBackend.SetVolumeAsync(normalizedVolume, ct)
                : Task.CompletedTask,
            ct);
    }

    public async Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        // Deliberately checked before acquiring the gate: this is what stops the position
        // timer (polling every ~150ms) from queuing up behind OpenAsync's gate-free slow
        // phase — it now returns Empty immediately during Opening instead of blocking on it.
        if (state == BackendState.Opening)
        {
            return PlaybackSnapshot.Empty;
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return backend is null
                ? PlaybackSnapshot.Empty
                : await backend.GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Bump the generation so an in-flight OpenAsync's gate-free phase (started before
            // this Reset) is recognized as stale when its Phase 3 re-acquires the gate, instead
            // of resurrecting a backend this Reset just tore down.
            Interlocked.Increment(ref openGeneration);
            state = BackendState.None;
            CurrentAssessment = null;
            pendingFrameSink = null;
            ReplaceBackend(null);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunWithBackendAsync(Func<IPlaybackBackend, Task> action, CancellationToken ct)
    {
        // Seek/Play/Pause/etc. against a backend that's still mid-open would either block on
        // the gate for the remainder of the open or race the open's own state transitions.
        // The transport CanExecute already prevents user-initiated clicks during Opening; this
        // is the service-level backstop for callers (e.g. the position timer path) that don't
        // go through CanExecute.
        if (state == BackendState.Opening)
        {
            return;
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (backend is not null)
            {
                await action(backend).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void ReplaceBackend(IPlaybackBackend? nextBackend)
    {
        if (!ReferenceEquals(backend, nextBackend) &&
            backend is IDisposable disposableBackend)
        {
            disposableBackend.Dispose();
        }

        backend = nextBackend;
        AttachPendingFrameSinkToCurrentBackend();
    }

    private bool AttachPendingFrameSinkToCurrentBackend()
    {
        if (pendingFrameSink is null ||
            backend is not IPlaybackFrameSinkAwareBackend frameSinkAwareBackend)
        {
            return false;
        }

        return frameSinkAwareBackend.TryAttachFrameSink(pendingFrameSink);
    }

    private static string BuildBackendUnavailableWarning(PlaybackCapabilityAssessment assessment) =>
        assessment.PreferredBackend switch
        {
            PlaybackBackendKind.FfmpegFallback =>
                "FFmpeg fallback is required for this source, but that backend is not implemented in this build.",
            PlaybackBackendKind.LibMpvFallback =>
                "libmpv fallback is required for this source, but that backend is not implemented in this build.",
            PlaybackBackendKind.LibMpv =>
                "libmpv runtime is not available. Ensure the bundled libmpv runtime is present in the application directory.",
            PlaybackBackendKind.MediaFoundation =>
                "Media Foundation playback is not available in this build.",
            PlaybackBackendKind.LibVlc =>
                "LibVLC runtime is not available. Ensure the bundled libvlc runtime is present in the application directory.",
            _ =>
                "The selected playback backend is not available in this build."
        };
}

public sealed record WaveformSegmentBoundary(
    double StartSeconds,
    double EndSeconds);

public sealed record WaveformBarLayout(
    float X,
    float TopY,
    float BottomY,
    float StrokeWidth);

public sealed record WaveformCanvasLayout(
    IReadOnlyList<WaveformBarLayout> Bars,
    IReadOnlyList<float> SegmentStartMarkerXs,
    IReadOnlyList<float> SegmentEndMarkerXs,
    float CursorX);

public static class WaveformLayout
{
    public static WaveformCanvasLayout Build(
        WaveformSummary waveform,
        IReadOnlyList<WaveformSegmentBoundary> segments,
        double playbackPositionSeconds,
        float width,
        float height)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        ArgumentNullException.ThrowIfNull(segments);

        if (waveform.Peaks.Count == 0 || width <= 0f || height <= 0f)
        {
            return new WaveformCanvasLayout(
                Array.Empty<WaveformBarLayout>(),
                Array.Empty<float>(),
                Array.Empty<float>(),
                0f);
        }

        int sourcePeakCount = waveform.Peaks.Count;
        int renderBarCount = Math.Min(sourcePeakCount, Math.Max(1, (int)Math.Ceiling(width)));
        float centerY = height / 2f;
        float step = width / renderBarCount;
        var bars = new List<WaveformBarLayout>(renderBarCount);

        for (int index = 0; index < renderBarCount; index++)
        {
            int sourceStartIndex = (int)Math.Floor(((double)index * sourcePeakCount) / renderBarCount);
            int sourceEndIndex = Math.Min(
                sourcePeakCount,
                Math.Max(sourceStartIndex + 1, (int)Math.Floor(((double)(index + 1) * sourcePeakCount) / renderBarCount)));
            float amplitude = 0f;
            for (int sourceIndex = sourceStartIndex; sourceIndex < sourceEndIndex; sourceIndex++)
            {
                amplitude = Math.Max(amplitude, Math.Clamp(waveform.Peaks[sourceIndex], 0f, 1f));
            }

            float barHeight = Math.Max(1f, amplitude * height);
            float x = index * step;
            bars.Add(new WaveformBarLayout(
                x,
                centerY - (barHeight / 2f),
                centerY + (barHeight / 2f),
                Math.Max(1f, step * 0.6f)));
        }

        float[] startMarkers = segments
            .Select(segment => WaveformMapping.TimeToPixel(segment.StartSeconds, waveform.DurationSeconds, width))
            .ToArray();
        float[] endMarkers = segments
            .Select(segment => WaveformMapping.TimeToPixel(segment.EndSeconds, waveform.DurationSeconds, width))
            .ToArray();
        float cursorX = WaveformMapping.TimeToPixel(playbackPositionSeconds, waveform.DurationSeconds, width);

        return new WaveformCanvasLayout(bars, startMarkers, endMarkers, cursorX);
    }
}

public static class WaveformMapping
{
    public static float TimeToPixel(double timeSeconds, double durationSeconds, float width)
    {
        if (!double.IsFinite(timeSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0d || width <= 0f)
        {
            return 0f;
        }

        double ratio = Math.Clamp(timeSeconds / durationSeconds, 0d, 1d);
        return (float)(ratio * width);
    }

    public static double PixelToTime(float pixel, double durationSeconds, float width)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0d || width <= 0f)
        {
            return 0d;
        }

        double ratio = Math.Clamp(pixel / width, 0f, 1f);
        return ratio * durationSeconds;
    }
}

public sealed class DefaultPlaybackBackendFactory : IPlaybackBackendFactory
{
    public IPlaybackBackend? Create(PlaybackBackendKind backendKind)
    {
#if WINDOWS
        return backendKind == PlaybackBackendKind.MediaFoundation
            ? new MediaFoundationPlaybackBackend()
            : null;
#else
        return null;
#endif
    }
}
