using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using Trackdub.Contracts;
using Trackdub.Domain.Media;

namespace Trackdub.Media.Playback;

/// <summary>
/// libmpv-backed playback backend that renders video frames into an app-owned software buffer
/// and forwards them through the frame-sink seam for Avalonia composition.
/// </summary>
public sealed class LibMpvCompositedPlaybackBackend :
    IPlaybackBackend,
    IPlaybackRateBackend,
    IPlaybackVolumeBackend,
    IPlaybackFrameSinkAwareBackend,
    IPlaybackPreviewFrameBackend,
    IDisposable
{
    private const string ApiTypeSw = "sw";
    private const string PixelFormat = "bgr0";
    private const int PixelSizeBytes = 4;
    private const int RenderUpdateFrame = 1;
    private const int RenderParamApiType = 1;
    private const int RenderParamAdvancedControl = 6;
    private const int RenderParamSwSize = 17;
    private const int RenderParamSwFormat = 18;
    private const int RenderParamSwStride = 19;
    private const int RenderParamSwPointer = 20;
    private const int MpvFormatDouble = 5;
    private const ulong DurationObserveReplyUserData = 1;

    // Reuse per-frame pixel buffers instead of allocating a fresh (large-object-heap) array every
    // render tick. At 1080p a frame is ~8 MB; allocating one per frame produced ~240 MB/s of LOH
    // churn, driving Gen2 collections that froze the UI thread and eventually exhausted memory.
    private static readonly ArrayPool<byte> FramePool = ArrayPool<byte>.Shared;

    private readonly string runtimeLibraryPath;
    private readonly PlaybackRuntimeOptions playbackRuntimeOptions;
    private readonly object sync = new();
    private readonly AutoResetEvent renderSignal = new(false);
    private readonly ManualResetEventSlim frameReadySignal = new(false);
    private readonly ManualResetEventSlim mediaReadyWakeupSignal = new(false);
    private readonly MpvRenderUpdateFn renderUpdateCallback;
    private readonly MpvWakeupCallback mediaReadyWakeupCallback;

    private IntPtr dllHandle;
    private IntPtr mpvHandle = IntPtr.Zero;
    private IntPtr renderContext = IntPtr.Zero;
    private Task? renderLoopTask;
    private CancellationTokenSource? renderLoopCancellation;
    private IPlaybackFrameSink? frameSink;
    private string? warningMessage;
    private IntPtr renderApiTypePtr = IntPtr.Zero;
    private IntPtr renderPixelFormatPtr = IntPtr.Zero;
    private IntPtr frameSizePtr = IntPtr.Zero;
    private IntPtr frameStridePtr = IntPtr.Zero;
    private IntPtr frameBuffer = IntPtr.Zero;
    private int frameWidth;
    private int frameHeight;
    private int frameStride;
    private int frameBufferBytes;
    private long frameBufferMemoryPressure;
    private volatile bool resizing;
    private bool usingStubDimensions;
    private bool isLoaded;
    private bool forceRenderRequested;
    private bool disposed;
    private double fallbackDurationSeconds;
    private bool usingSoftwareDecode;
    private int softwareDecodeRecoveryScheduled;
    private string? pendingSoftwareDecodeRecoveryPath;
    private string? pendingSoftwareDecodeRecoveryMessage;
    private string? loadedSourcePath;

    private MpvCreateFn? mpv_create;
    private MpvInitializeFn? mpv_initialize;
    private MpvSetOptionStringFn? mpv_set_option_string;
    private MpvCommandStringFn? mpv_command_string;
    private MpvGetPropertyStringFn? mpv_get_property_string;
    private MpvFreeFn? mpv_free;
    private MpvTerminateDestroyFn? mpv_terminate_destroy;
    private MpvRenderContextCreateFn? mpv_render_context_create;
    private MpvRenderContextFreeFn? mpv_render_context_free;
    private MpvRenderContextSetUpdateCallbackFn? mpv_render_context_set_update_callback;
    private MpvRenderContextUpdateFn? mpv_render_context_update;
    private MpvRenderContextRenderFn? mpv_render_context_render;
    private MpvObservePropertyFn? mpv_observe_property;
    private MpvSetWakeupCallbackFn? mpv_set_wakeup_callback;

    public LibMpvCompositedPlaybackBackend(string runtimeLibraryPath)
        : this(runtimeLibraryPath, new PlaybackRuntimeOptions())
    {
    }

    public LibMpvCompositedPlaybackBackend(
        string runtimeLibraryPath,
        PlaybackRuntimeOptions playbackRuntimeOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeLibraryPath);
        ArgumentNullException.ThrowIfNull(playbackRuntimeOptions);
        this.runtimeLibraryPath = runtimeLibraryPath;
        this.playbackRuntimeOptions = playbackRuntimeOptions;
        renderUpdateCallback = OnRenderUpdateRequested;
        mediaReadyWakeupCallback = OnMpvWakeup;
    }

    public bool TryAttachFrameSink(IPlaybackFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (sync)
        {
            frameSink = sink;
            if (frameWidth > 0 && frameHeight > 0 && frameStride > 0)
            {
                sink.OnVideoFormatChanged(new VideoFrameDescriptor(frameWidth, frameHeight, frameStride, PixelFormat));
            }

            forceRenderRequested = true;
        }

        renderSignal.Set();
        return true;
    }

    public async Task OpenAsync(MediaSourceDescriptor source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.SourcePath);

        _ = ReleaseResources();
        warningMessage = null;
        fallbackDurationSeconds = source.Probe.DurationSeconds;

        if (!File.Exists(source.SourcePath))
        {
            warningMessage = $"Source file not found: {source.SourcePath}";
            return;
        }

        if (source.Probe.VideoStreams.Count == 0)
        {
            warningMessage = "Composited playback requires a probed video stream.";
            return;
        }

        MediaVideoStream? primaryVideo = source.Probe.VideoStreams.FirstOrDefault();
        frameWidth = primaryVideo?.Width ?? 0;
        frameHeight = primaryVideo?.Height ?? 0;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            // Stale or partial probe metadata should not block playback when the file has video.
            frameWidth = 1280;
            frameHeight = 720;
            warningMessage =
                "Video dimensions were missing from probe metadata; using a temporary size until libmpv reports the stream geometry.";
        }

        frameStride = checked(frameWidth * PixelSizeBytes);

        string sourcePath = source.SourcePath;

        loadedSourcePath = sourcePath;

        try
        {
            await Task.Run(
                () =>
                {
                    ct.ThrowIfCancellationRequested();
                    MediaPlaybackRuntimeState runtime = playbackRuntimeOptions.Snapshot;
                    bool forceSoftwareDecode = ShouldForceSoftwareDecodeOnFirstAttempt(runtime);
                    if (!TryInitializePlaybackCore(sourcePath, forceSoftwareDecode, ct, runtime))
                    {
                        if (runtime.VideoDecodePreference == PlaybackVideoDecodePreference.Auto &&
                            !forceSoftwareDecode &&
                            TryInitializePlaybackCore(sourcePath, forceSoftwareDecode: true, ct, runtime))
                        {
                            warningMessage =
                                "Hardware preview decode unavailable; using software decode.";
                            return;
                        }

                        throw new InvalidOperationException(
                            warningMessage ?? "libmpv compositor initialization failed.");
                    }
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            warningMessage = "Open was canceled.";
            _ = ReleaseResources();
            throw;
        }
        catch (Exception ex)
        {
            warningMessage = $"libmpv compositor initialization failed: {ex.Message}";
            _ = ReleaseResources();
        }
    }

    public Task PreparePreviewFrameAsync(CancellationToken ct) =>
        Task.Run(
            () =>
            {
                if (!isLoaded)
                {
                    return;
                }

                ct.ThrowIfCancellationRequested();

                // Reset before triggering the render so we wait for the frame this seek
                // produces, not a stale signal left over from steady-state playback. Do NOT
                // wait on renderSignal here — it's an AutoResetEvent consumed by the render
                // loop; stealing its signal would starve a pending render tick.
                frameReadySignal.Reset();
                PreparePausedPreviewFrame();
                frameReadySignal.Wait(TimeSpan.FromMilliseconds(500), ct);
            },
            ct);

    public Task PlayAsync(CancellationToken ct)
    {
        ExecuteCommand("set pause no");
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        ExecuteCommand("set pause yes");
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        double positionSeconds = Math.Max(0d, position.TotalSeconds);
        ExecuteCommand(FormattableString.Invariant($"seek {positionSeconds:F3} absolute"));
        forceRenderRequested = true;
        renderSignal.Set();
        return Task.CompletedTask;
    }

    public Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (!isLoaded || mpvHandle == IntPtr.Zero)
        {
            return Task.FromResult(PlaybackSnapshot.Empty with { WarningMessage = warningMessage });
        }

        double positionSeconds = ReadDoubleProperty("time-pos");
        double durationSeconds = ReadDoubleProperty("duration");
        double speed = ReadDoubleProperty("speed");
        bool paused = ReadBooleanProperty("pause");

        return Task.FromResult(new PlaybackSnapshot(
            IsLoaded: true,
            IsPlaying: !paused,
            Position: TimeSpan.FromSeconds(positionSeconds),
            Duration: TimeSpan.FromSeconds(durationSeconds > 0d ? durationSeconds : fallbackDurationSeconds),
            PlaybackRate: speed > 0d ? speed : 1d,
            WarningMessage: warningMessage));
    }

    public Task SetPlaybackRateAsync(double playbackRate, CancellationToken ct)
    {
        double normalized = double.IsFinite(playbackRate) && playbackRate > 0d ? playbackRate : 1d;
        ExecuteCommand(FormattableString.Invariant($"set speed {normalized:F3}"));
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct)
    {
        int normalized = (int)Math.Round(Math.Clamp(volume, 0d, 1d) * 100d, MidpointRounding.AwayFromZero);
        ExecuteCommand(FormattableString.Invariant($"set volume {normalized}"));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        // ReleaseResources is non-blocking and defers native teardown until the render loop drains.
        // Disposal is the one place we must wait for that drain to complete before disposing the
        // signal the loop waits on, otherwise a still-running loop could touch a disposed handle.
        Task cleanup = ReleaseResources();
        try
        {
            cleanup.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Native cleanup faults are swallowed during disposal; nothing actionable remains.
        }

        renderSignal.Dispose();
        frameReadySignal.Dispose();
        mediaReadyWakeupSignal.Dispose();
    }

    private void LoadNativeLibrary()
    {
        if (dllHandle != IntPtr.Zero)
        {
            return;
        }

        dllHandle = LibMpvNativeLibrary.EnsureLoaded(runtimeLibraryPath);
        mpv_create = GetDelegate<MpvCreateFn>("mpv_create");
        mpv_initialize = GetDelegate<MpvInitializeFn>("mpv_initialize");
        mpv_set_option_string = GetDelegate<MpvSetOptionStringFn>("mpv_set_option_string");
        mpv_command_string = GetDelegate<MpvCommandStringFn>("mpv_command_string");
        mpv_get_property_string = GetDelegate<MpvGetPropertyStringFn>("mpv_get_property_string");
        mpv_free = GetDelegate<MpvFreeFn>("mpv_free");
        mpv_terminate_destroy = GetDelegate<MpvTerminateDestroyFn>("mpv_terminate_destroy");
        mpv_render_context_create = GetDelegate<MpvRenderContextCreateFn>("mpv_render_context_create");
        mpv_render_context_free = GetDelegate<MpvRenderContextFreeFn>("mpv_render_context_free");
        mpv_render_context_set_update_callback = GetDelegate<MpvRenderContextSetUpdateCallbackFn>("mpv_render_context_set_update_callback");
        mpv_render_context_update = GetDelegate<MpvRenderContextUpdateFn>("mpv_render_context_update");
        mpv_render_context_render = GetDelegate<MpvRenderContextRenderFn>("mpv_render_context_render");

        // Optional: event-driven media-ready notification. Both stable since libmpv 0.29+
        // (we ship 0.37+), but bind defensively via TryGetExport — WaitForMediaReady falls
        // back to its Thread.Sleep(25) poll loop if either export is missing.
        mpv_observe_property = TryGetDelegate<MpvObservePropertyFn>("mpv_observe_property");
        mpv_set_wakeup_callback = TryGetDelegate<MpvSetWakeupCallbackFn>("mpv_set_wakeup_callback");
    }

    private T? TryGetDelegate<T>(string exportName) where T : Delegate =>
        NativeLibrary.TryGetExport(dllHandle, exportName, out IntPtr functionPointer)
            ? Marshal.GetDelegateForFunctionPointer<T>(functionPointer)
            : null;

    private bool TryInitializePlaybackCore(
        string sourcePath,
        bool forceSoftwareDecode,
        CancellationToken cancellationToken,
        MediaPlaybackRuntimeState runtime)
    {
        int preservedWidth = frameWidth;
        int preservedHeight = frameHeight;
        int preservedStride = frameStride;

        _ = ReleaseResources();

        if (preservedWidth > 0 && preservedHeight > 0 && preservedStride > 0)
        {
            frameWidth = preservedWidth;
            frameHeight = preservedHeight;
            frameStride = preservedStride;
        }

        usingSoftwareDecode = forceSoftwareDecode;

        try
        {
            LoadNativeLibrary();
            CreatePlayerCore(forceSoftwareDecode, runtime);
            LoadSourceMedia(sourcePath);
            WaitForMediaReady(cancellationToken);
            ApplyPlayerDimensionsFromProperties();
            CreateRenderTargetBuffers();
            CreateRenderContext();
            StartRenderLoop();
            PreparePausedPreviewFrame();

            lock (sync)
            {
                isLoaded = true;
                frameSink?.OnVideoFormatChanged(
                    new VideoFrameDescriptor(frameWidth, frameHeight, frameStride, PixelFormat));
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            _ = ReleaseResources();
            throw;
        }
        catch (Exception ex)
        {
            warningMessage = ex.Message;
            _ = ReleaseResources();
            return false;
        }
    }

    private static bool ShouldForceSoftwareDecodeOnFirstAttempt(MediaPlaybackRuntimeState runtime)
    {
        if (runtime.VideoDecodePreference == PlaybackVideoDecodePreference.Software)
        {
            return true;
        }

        return runtime.GpuHint is { HasGpu: false };
    }

    private void CreatePlayerCore(bool forceSoftwareDecode, MediaPlaybackRuntimeState runtime)
    {
        renderApiTypePtr = Marshal.StringToHGlobalAnsi(ApiTypeSw);
        renderPixelFormatPtr = Marshal.StringToHGlobalAnsi(PixelFormat);

        mpvHandle = mpv_create!.Invoke();
        if (mpvHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create libmpv context.");
        }

        SetOption("vo", "libmpv");
        SetOption("hwdec", LibMpvPlaybackOptions.ResolveHwdecOption(
            forceSoftwareDecode
                ? PlaybackVideoDecodePreference.Software
                : runtime.VideoDecodePreference,
            runtime.FfmpegEncoderSnapshot,
            runtime.GpuHint));
        SetOption("idle", "yes");
        SetOption("keep-open", "yes");
        SetOption("pause", "yes");
        SetOption("osd-level", "0");
        SetOption("sub", "no");
        SetOption("audio-display", "no");

        int initResult = mpv_initialize!.Invoke(mpvHandle);
        if (initResult < 0)
        {
            throw new InvalidOperationException($"libmpv initialization failed with error code {initResult}.");
        }

        mediaReadyWakeupSignal.Reset();
        if (mpv_observe_property is not null && mpv_set_wakeup_callback is not null)
        {
            mpv_set_wakeup_callback.Invoke(mpvHandle, mediaReadyWakeupCallback, IntPtr.Zero);
            mpv_observe_property.Invoke(mpvHandle, DurationObserveReplyUserData, "duration", MpvFormatDouble);
        }
    }

    // Fires on libmpv's internal event thread for ANY queued event (property change, file-loaded,
    // etc.) — per libmpv's contract this callback must not call back into the mpv API (not even to
    // read a property), so it only flips a lightweight signal. WaitForMediaReady wakes up and does
    // the actual (safe, same-thread) property reads itself.
    private void OnMpvWakeup(IntPtr callbackContext) => mediaReadyWakeupSignal.Set();

    // Allocates the native software render buffer and the SW size/stride descriptors from the
    // CURRENT frameWidth/frameHeight/frameStride. The buffer, the advertised SW size, and the stride
    // are always written together here, so they can never disagree — the invariant that prevents
    // both the managed overread in RenderCurrentFrame and the native heap overwrite inside
    // mpv_render_context_render. Callers that change the dimensions must call this to re-sync.
    private void CreateRenderTargetBuffers()
    {
        resizing = true;
        try
        {
            FreeRenderTargetBuffers();

            int bufferBytes = checked(frameStride * frameHeight);
            frameBuffer = Marshal.AllocHGlobal(bufferBytes);
            if (frameBuffer == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to allocate the libmpv software render buffer.");
            }

            frameBufferBytes = bufferBytes;
            GC.AddMemoryPressure(bufferBytes);
            frameBufferMemoryPressure = bufferBytes;

            frameSizePtr = Marshal.AllocHGlobal(sizeof(int) * 2);
            Marshal.WriteInt32(frameSizePtr, 0, frameWidth);
            Marshal.WriteInt32(frameSizePtr, sizeof(int), frameHeight);

            frameStridePtr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(frameStridePtr, new IntPtr(frameStride));
        }
        finally
        {
            resizing = false;
        }
    }

    private void CreateRenderContext()
    {
        MpvRenderParam[] parameters =
        [
            new MpvRenderParam(RenderParamApiType, renderApiTypePtr),
            new MpvRenderParam(RenderParamAdvancedControl, Marshal.AllocHGlobal(sizeof(int))),
            new MpvRenderParam(0, IntPtr.Zero),
        ];

        try
        {
            Marshal.WriteInt32(parameters[1].Data, 1);
            int result = mpv_render_context_create!(out renderContext, mpvHandle, parameters);
            if (result < 0 || renderContext == IntPtr.Zero)
            {
                throw new InvalidOperationException($"libmpv render context creation failed with error code {result}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(parameters[1].Data);
        }

        mpv_render_context_set_update_callback!(renderContext, renderUpdateCallback, IntPtr.Zero);
    }

    private void LoadSourceMedia(string sourcePath)
    {
        string normalized = sourcePath.Replace("\\", "/", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        int result = mpv_command_string!.Invoke(mpvHandle, $"loadfile \"{normalized}\"");
        if (result < 0)
        {
            throw new InvalidOperationException($"libmpv failed to load media with error code {result}.");
        }
    }

    // Reads the real geometry libmpv decoded and applies it. Returns true once libmpv reports valid
    // dimensions; false while they are still unavailable (in which case the caller keeps the probe /
    // stub size and retries after the first frame). When the dimensions change after the render
    // buffers already exist, the buffers are reallocated here under the same lock so the native
    // allocation and the advertised SW size stay consistent.
    private bool ApplyPlayerDimensionsFromProperties()
    {
        if (mpvHandle == IntPtr.Zero)
        {
            return false;
        }

        int playerWidth = Math.Max(0, (int)Math.Round(ReadDoubleProperty("width"), MidpointRounding.AwayFromZero));
        int playerHeight = Math.Max(0, (int)Math.Round(ReadDoubleProperty("height"), MidpointRounding.AwayFromZero));

        lock (sync)
        {
            if (playerWidth <= 0 || playerHeight <= 0)
            {
                usingStubDimensions = true;
                return false;
            }

            bool dimensionsChanged = playerWidth != frameWidth || playerHeight != frameHeight;
            frameWidth = playerWidth;
            frameHeight = playerHeight;
            frameStride = checked(frameWidth * PixelSizeBytes);
            usingStubDimensions = false;

            // If the render buffers were already allocated at the previous size, resize them now so
            // frameBuffer, frameSizePtr and frameStridePtr describe the new geometry together.
            if (dimensionsChanged && frameBuffer != IntPtr.Zero)
            {
                CreateRenderTargetBuffers();
            }

            return true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

    private void WaitForMediaReady(CancellationToken cancellationToken)
    {
        bool eventDriven = mpv_observe_property is not null && mpv_set_wakeup_callback is not null;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? loadedPath = ReadPropertyString("path");
            if (!string.IsNullOrWhiteSpace(loadedPath))
            {
                double durationSeconds = ReadDoubleProperty("duration");
                if (durationSeconds > 0d || fallbackDurationSeconds > 0d)
                {
                    return;
                }
            }

            if (eventDriven)
            {
                // Block until the next libmpv event (property change, file-loaded, ...) instead of
                // spinning every 25ms; re-check the properties above once woken (or once the
                // remaining deadline elapses, so a missed/coalesced wakeup can't hang this past 15s).
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                mediaReadyWakeupSignal.Wait(remaining, cancellationToken);
                mediaReadyWakeupSignal.Reset();
            }
            else
            {
                Thread.Sleep(25);
            }
        }

        throw new TimeoutException("Timed out waiting for libmpv to finish loading the source media.");
    }

    private void PreparePausedPreviewFrame()
    {
        ExecuteCommand("set pause yes");
        ExecuteCommand("seek 0 absolute+keyframes");
        lock (sync)
        {
            forceRenderRequested = true;
        }

        renderSignal.Set();
    }

    private void StartRenderLoop()
    {
        var cancellation = new CancellationTokenSource();
        renderLoopCancellation = cancellation;
        renderLoopTask = Task.Run(() => RenderLoop(cancellation.Token));
    }

    private void RenderLoop(CancellationToken cancellationToken)
    {
        try
        {
            RenderLoopCore(cancellationToken);
        }
        finally
        {
            // Any software-decode recovery requested from inside the loop runs here, AFTER the loop
            // has fully exited on this thread. That guarantees the re-initialization (which tears down
            // and rebuilds the mpv handle and render context) never overlaps a still-executing
            // RenderCurrentFrame on this same render-loop thread — the use-after-free that a direct
            // Task.Run from the catch block allowed.
            TrySoftwareDecodeRecoveryAfterLoop();
        }
    }

    private void RenderLoopCore(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            renderSignal.WaitOne(TimeSpan.FromMilliseconds(100));
            if (cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            IntPtr context;
            lock (sync)
            {
                context = renderContext;
            }

            // Skip a tick while the render buffers are being reallocated (resizing) so we never render
            // against a geometry that does not match the live native allocation.
            if (context == IntPtr.Zero || resizing)
            {
                continue;
            }

            bool forceRender;
            lock (sync)
            {
                forceRender = forceRenderRequested;
                forceRenderRequested = false;
            }

            uint updateFlags = mpv_render_context_update!.Invoke(context);
            if ((updateFlags & RenderUpdateFrame) == 0 && !forceRender)
            {
                continue;
            }

            try
            {
                RenderCurrentFrame();
            }
            catch (Exception ex)
            {
                if (!usingSoftwareDecode &&
                    playbackRuntimeOptions.Snapshot.VideoDecodePreference == PlaybackVideoDecodePreference.Auto &&
                    !string.IsNullOrWhiteSpace(loadedSourcePath) &&
                    Interlocked.CompareExchange(ref softwareDecodeRecoveryScheduled, 1, 0) == 0)
                {
                    // Defer the actual recovery until the loop has fully drained (handled in RenderLoop).
                    // Cancelling and exiting here lets this render tick unwind before
                    // TryInitializePlaybackCore tears the mpv handle and render context down, so recovery
                    // can never run alongside a live RenderCurrentFrame on this thread.
                    pendingSoftwareDecodeRecoveryPath = loadedSourcePath;
                    pendingSoftwareDecodeRecoveryMessage = ex.Message;
                    renderLoopCancellation?.Cancel();
                    return;
                }

                warningMessage = $"libmpv frame rendering failed: {ex.Message}";
            }
        }
    }

    private void TrySoftwareDecodeRecoveryAfterLoop()
    {
        string? recoveryPath = pendingSoftwareDecodeRecoveryPath;
        if (recoveryPath is null)
        {
            return;
        }

        pendingSoftwareDecodeRecoveryPath = null;
        string? failureMessage = pendingSoftwareDecodeRecoveryMessage;
        pendingSoftwareDecodeRecoveryMessage = null;

        try
        {
            if (disposed)
            {
                return;
            }

            MediaPlaybackRuntimeState runtime = playbackRuntimeOptions.Snapshot;
            if (TryInitializePlaybackCore(recoveryPath, forceSoftwareDecode: true, CancellationToken.None, runtime))
            {
                warningMessage = "Hardware preview decode failed during rendering; using software decode.";
            }
            else
            {
                warningMessage = $"libmpv frame rendering failed: {failureMessage}";
            }
        }
        finally
        {
            Interlocked.Exchange(ref softwareDecodeRecoveryScheduled, 0);
        }
    }

    // Bytes required for a bgr0 frame of the given geometry. Used to size the native render buffer;
    // when the source resolution changes the buffer must be reallocated to the new value before the
    // new geometry is advertised to libmpv (the ResolutionChange guard).
    internal static int ComputeFrameBufferBytes(int width, int height) =>
        checked(Math.Max(0, width) * PixelSizeBytes * Math.Max(0, height));

    // Clamps the managed copy length to what the native render buffer actually holds, so a stale or
    // undersized buffer can never produce an out-of-bounds read from unmanaged memory (the
    // BufferOverread guard for BLOCKER B/I). In the steady state required == allocated and this is a
    // no-op; it only bites when the two have drifted.
    internal static int ClampFrameCopyLength(int requiredFrameBytes, int allocatedBufferBytes) =>
        Math.Min(Math.Max(0, requiredFrameBytes), Math.Max(0, allocatedBufferBytes));

    // Path selection for RenderCurrentFrame, extracted as a pure decision so a regression test can
    // pin that ONLY an IPlaybackDirectRenderTarget sink takes the zero-copy path, and every other
    // sink (the LibVLC presenter, or any future sink that cannot lend a render target) keeps the
    // copy-based IPlaybackFrameSink path. This is the safety net against a silent LibVLC regression.
    internal static bool ShouldUseDirectRenderPath(IPlaybackFrameSink? sink) =>
        sink is IPlaybackDirectRenderTarget;

    private void RenderCurrentFrame()
    {
        // Snapshot the sink, the native handles, the SW descriptors and the geometry together under
        // the lock so the render parameters, the managed copy length and the frame descriptor all
        // describe one consistent allocation, even if a reallocation or teardown races this tick.
        IPlaybackFrameSink? sink;
        IntPtr context;
        IntPtr buffer;
        IntPtr sizePtr;
        IntPtr stridePtr;
        IntPtr pixelFormatPtr;
        int width;
        int height;
        int stride;
        int bufferBytes;
        bool stubDimensions;
        lock (sync)
        {
            sink = frameSink;
            context = renderContext;
            buffer = frameBuffer;
            sizePtr = frameSizePtr;
            stridePtr = frameStridePtr;
            pixelFormatPtr = renderPixelFormatPtr;
            width = frameWidth;
            height = frameHeight;
            stride = frameStride;
            bufferBytes = frameBufferBytes;
            stubDimensions = usingStubDimensions;
        }

        if (sink is null ||
            context == IntPtr.Zero ||
            pixelFormatPtr == IntPtr.Zero)
        {
            return;
        }

        bool rendered = ShouldUseDirectRenderPath(sink)
            ? RenderCurrentFrameToDirectTarget((IPlaybackDirectRenderTarget)sink, context, pixelFormatPtr)
            : RenderCurrentFrameToManagedSink(
                sink, context, buffer, sizePtr, stridePtr, pixelFormatPtr, width, height, stride, bufferBytes);

        if (!rendered)
        {
            return;
        }

        frameReadySignal.Set();

        // If we started before libmpv reported its geometry, we have now delivered a frame at the
        // temporary size. Retry picking up the real dimensions — ApplyPlayerDimensionsFromProperties
        // reallocates the buffers if they changed — so subsequent frames render at full resolution.
        if (stubDimensions && ApplyPlayerDimensionsFromProperties())
        {
            IPlaybackFrameSink? formatSink;
            VideoFrameDescriptor descriptor;
            lock (sync)
            {
                formatSink = frameSink;
                descriptor = new VideoFrameDescriptor(frameWidth, frameHeight, frameStride, PixelFormat);
            }

            formatSink?.OnVideoFormatChanged(descriptor);
        }
    }

    // Zero-copy path (libmpv): render straight into the presenter's locked back buffer, with no
    // managed array, no Marshal.Copy and no ArrayPool rent. The SW size/stride are advertised from
    // the borrowed lock's geometry (its RowBytes may differ from frameWidth*4 due to bitmap row
    // alignment), and the opaque-alpha fix is applied in place on the native destination. Disposing
    // the lock presents the frame. Returns false (skip this tick) when the back buffer is not ready.
    private unsafe bool RenderCurrentFrameToDirectTarget(
        IPlaybackDirectRenderTarget target,
        IntPtr context,
        IntPtr pixelFormatPtr)
    {
        using DirectRenderLock renderLock = target.AcquireRenderLock();
        if (renderLock.Buffer == IntPtr.Zero ||
            renderLock.Width <= 0 ||
            renderLock.Height <= 0 ||
            renderLock.Stride <= 0)
        {
            return false;
        }

        int* sizeBuffer = stackalloc int[2];
        sizeBuffer[0] = renderLock.Width;
        sizeBuffer[1] = renderLock.Height;
        nint strideValue = renderLock.Stride;

        MpvRenderParam[] renderParameters =
        [
            new MpvRenderParam(RenderParamSwSize, (IntPtr)sizeBuffer),
            new MpvRenderParam(RenderParamSwFormat, pixelFormatPtr),
            new MpvRenderParam(RenderParamSwStride, (IntPtr)(&strideValue)),
            new MpvRenderParam(RenderParamSwPointer, renderLock.Buffer),
            new MpvRenderParam(0, IntPtr.Zero),
        ];

        int renderResult = mpv_render_context_render!.Invoke(context, renderParameters);
        if (renderResult < 0)
        {
            warningMessage = $"libmpv render returned error code {renderResult}.";
            return false;
        }

        // bgr0 leaves the alpha byte at 0; force it opaque in place so the composited frame is
        // visible. The destination is the WriteableBitmap back buffer, valid until the lock disposes.
        byte* pixels = (byte*)renderLock.Buffer;
        long length = (long)renderLock.Stride * renderLock.Height;
        for (long index = 3; index < length; index += PixelSizeBytes)
        {
            pixels[index] = 255;
        }

        return true;
    }

    // Copy-based path (LibVLC and any non-direct sink): render into the app-owned native buffer, then
    // copy into a pooled managed array, force opaque alpha, and hand it to the frame sink synchronously.
    private bool RenderCurrentFrameToManagedSink(
        IPlaybackFrameSink sink,
        IntPtr context,
        IntPtr buffer,
        IntPtr sizePtr,
        IntPtr stridePtr,
        IntPtr pixelFormatPtr,
        int width,
        int height,
        int stride,
        int bufferBytes)
    {
        if (buffer == IntPtr.Zero ||
            sizePtr == IntPtr.Zero ||
            stridePtr == IntPtr.Zero)
        {
            return false;
        }

        MpvRenderParam[] renderParameters =
        [
            new MpvRenderParam(RenderParamSwSize, sizePtr),
            new MpvRenderParam(RenderParamSwFormat, pixelFormatPtr),
            new MpvRenderParam(RenderParamSwStride, stridePtr),
            new MpvRenderParam(RenderParamSwPointer, buffer),
            new MpvRenderParam(0, IntPtr.Zero),
        ];

        int renderResult = mpv_render_context_render!.Invoke(context, renderParameters);
        if (renderResult < 0)
        {
            warningMessage = $"libmpv render returned error code {renderResult}.";
            return false;
        }

        // Never copy more than the native buffer actually holds. Because frameSizePtr/frameStridePtr
        // are always written together with the allocation, in the steady state fullSize == bufferBytes;
        // the clamp is the last-line guard against a stale buffer producing an out-of-bounds read.
        int fullSize = checked(stride * height);
        int copySize = ClampFrameCopyLength(fullSize, bufferBytes);
        if (copySize != fullSize)
        {
            warningMessage =
                $"libmpv render buffer ({bufferBytes} bytes) is smaller than the current frame ({fullSize} bytes); clamping the copy to prevent an overread.";
        }

        byte[] managedFrame = FramePool.Rent(copySize);
        try
        {
            Marshal.Copy(buffer, managedFrame, 0, copySize);

            // bgr0 leaves the alpha byte at 0; force it opaque so the composited frame is visible.
            for (int index = 3; index < copySize; index += PixelSizeBytes)
            {
                managedFrame[index] = 255;
            }

            // The sink must consume the pixels synchronously: the rented buffer is returned to the
            // pool the instant this call returns and may be reused by the very next frame.
            sink.OnVideoFrameArrived(new VideoFrame(
                new VideoFrameDescriptor(width, height, stride, PixelFormat),
                managedFrame));
        }
        finally
        {
            FramePool.Return(managedFrame);
        }

        return true;
    }

    private void ExecuteCommand(string command)
    {
        if (mpvHandle == IntPtr.Zero || mpv_command_string is null)
        {
            return;
        }

        int result = mpv_command_string.Invoke(mpvHandle, command);
        if (result < 0)
        {
            warningMessage = $"libmpv command failed ({result}) while executing '{command}'.";
        }
    }

    private void SetOption(string name, string value)
    {
        int result = mpv_set_option_string!.Invoke(mpvHandle, name, value);
        if (result < 0)
        {
            throw new InvalidOperationException($"Failed to set libmpv option '{name}'='{value}' (error {result}).");
        }
    }

    private double ReadDoubleProperty(string propertyName)
    {
        string? raw = ReadPropertyString(propertyName);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0d;
    }

    private bool ReadBooleanProperty(string propertyName)
    {
        string? raw = ReadPropertyString(propertyName);
        return string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private string? ReadPropertyString(string propertyName)
    {
        if (mpvHandle == IntPtr.Zero || mpv_get_property_string is null)
        {
            return null;
        }

        IntPtr valuePtr = mpv_get_property_string.Invoke(mpvHandle, propertyName);
        if (valuePtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringAnsi(valuePtr);
        }
        finally
        {
            mpv_free?.Invoke(valuePtr);
        }
    }

    // Frees the render target buffers on the render/init thread during a reallocation. Full teardown
    // instead defers buffer frees to the post-drain continuation in ReleaseResources.
    private void FreeRenderTargetBuffers()
    {
        if (frameBuffer != IntPtr.Zero)
        {
            if (frameBufferMemoryPressure > 0)
            {
                GC.RemoveMemoryPressure(frameBufferMemoryPressure);
                frameBufferMemoryPressure = 0;
            }

            Marshal.FreeHGlobal(frameBuffer);
            frameBuffer = IntPtr.Zero;
        }

        frameBufferBytes = 0;

        if (frameSizePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(frameSizePtr);
            frameSizePtr = IntPtr.Zero;
        }

        if (frameStridePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(frameStridePtr);
            frameStridePtr = IntPtr.Zero;
        }
    }

    // Signals the render loop to stop and returns a task that completes once the native resources are
    // freed. It never blocks the caller: the loop is cancelled, the instance fields are cleared under
    // the lock so no new operation can touch a doomed handle, and the actual native frees are chained
    // onto the render loop's own task so they run only AFTER the loop has drained. This is what makes
    // switching videos non-blocking and stops native cleanup from racing an in-flight render tick.
    private Task ReleaseResources()
    {
        Interlocked.Exchange(ref softwareDecodeRecoveryScheduled, 0);

        Task? loopTask;
        CancellationTokenSource? cancellation;
        IntPtr context;
        IntPtr handle;
        IntPtr buffer;
        IntPtr sizePtr;
        IntPtr stridePtr;
        IntPtr apiTypePtr;
        IntPtr pixelFormatPtr;
        long memoryPressure;
        MpvRenderContextFreeFn? freeContext;
        MpvTerminateDestroyFn? terminate;
        MpvSetWakeupCallbackFn? clearWakeupCallback = mpv_set_wakeup_callback;

        lock (sync)
        {
            isLoaded = false;
            forceRenderRequested = false;
            usingStubDimensions = false;

            loopTask = renderLoopTask;
            cancellation = renderLoopCancellation;
            context = renderContext;
            handle = mpvHandle;
            buffer = frameBuffer;
            sizePtr = frameSizePtr;
            stridePtr = frameStridePtr;
            apiTypePtr = renderApiTypePtr;
            pixelFormatPtr = renderPixelFormatPtr;
            memoryPressure = frameBufferMemoryPressure;
            freeContext = mpv_render_context_free;
            terminate = mpv_terminate_destroy;

            renderLoopTask = null;
            renderLoopCancellation = null;
            renderContext = IntPtr.Zero;
            mpvHandle = IntPtr.Zero;
            frameBuffer = IntPtr.Zero;
            frameSizePtr = IntPtr.Zero;
            frameStridePtr = IntPtr.Zero;
            renderApiTypePtr = IntPtr.Zero;
            renderPixelFormatPtr = IntPtr.Zero;
            frameBufferBytes = 0;
            frameBufferMemoryPressure = 0;
            dllHandle = IntPtr.Zero;

            frameWidth = 0;
            frameHeight = 0;
            frameStride = 0;
        }

        // Wake the loop so it observes cancellation promptly and exits.
        cancellation?.Cancel();
        renderSignal.Set();
        frameReadySignal.Reset();

        void FreeNative()
        {
            try
            {
                if (context != IntPtr.Zero)
                {
                    freeContext?.Invoke(context);
                }

                if (handle != IntPtr.Zero)
                {
                    // Clear the wakeup callback before terminating — it fires on libmpv's internal
                    // event thread and must not run against a handle mid-teardown.
                    clearWakeupCallback?.Invoke(handle, null!, IntPtr.Zero);
                    terminate?.Invoke(handle);
                }

                if (buffer != IntPtr.Zero)
                {
                    if (memoryPressure > 0)
                    {
                        GC.RemoveMemoryPressure(memoryPressure);
                    }

                    Marshal.FreeHGlobal(buffer);
                }

                if (sizePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(sizePtr);
                }

                if (stridePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(stridePtr);
                }

                if (apiTypePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(apiTypePtr);
                }

                if (pixelFormatPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pixelFormatPtr);
                }
            }
            finally
            {
                cancellation?.Dispose();
            }
        }

        if (loopTask is null)
        {
            FreeNative();
            return Task.CompletedTask;
        }

        return loopTask.ContinueWith(
            static (_, state) => ((Action)state!).Invoke(),
            (Action)FreeNative,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private T GetDelegate<T>(string exportName) where T : Delegate
    {
        IntPtr functionPointer = NativeLibrary.GetExport(dllHandle, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(functionPointer);
    }

    private void OnRenderUpdateRequested(IntPtr context)
    {
        renderSignal.Set();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvRenderParam(int type, IntPtr data)
    {
        public int Type = type;
        public IntPtr Data = data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MpvCreateFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvInitializeFn(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetOptionStringFn(IntPtr handle, string name, string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvCommandStringFn(IntPtr handle, string command);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MpvGetPropertyStringFn(IntPtr handle, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvFreeFn(IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvTerminateDestroyFn(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvRenderContextCreateFn(out IntPtr renderContext, IntPtr mpvHandle, MpvRenderParam[] parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvRenderContextFreeFn(IntPtr renderContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvRenderContextSetUpdateCallbackFn(IntPtr renderContext, MpvRenderUpdateFn callback, IntPtr callbackContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint MpvRenderContextUpdateFn(IntPtr renderContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvRenderContextRenderFn(IntPtr renderContext, MpvRenderParam[] parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvRenderUpdateFn(IntPtr callbackContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvObservePropertyFn(IntPtr handle, ulong replyUserData, string name, int format);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvSetWakeupCallbackFn(IntPtr handle, MpvWakeupCallback callback, IntPtr callbackContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvWakeupCallback(IntPtr callbackContext);
}
