using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

/// <summary>
/// Pins the render-path selection for the libmpv compositor: the zero-copy direct-render path is
/// taken ONLY when the attached sink can lend a render target (<see cref="IPlaybackDirectRenderTarget"/>),
/// and every other sink keeps the copy-based <see cref="IPlaybackFrameSink"/> path. This is the safety
/// net against a silent LibVLC regression if the interface-detection logic is ever changed to, e.g., a
/// type-name switch. It proves which branch is chosen without any native libmpv or real rendering.
/// </summary>
public sealed class LibMpvCompositedPlaybackDirectRenderTests
{
    [Fact]
    public void Direct_render_path_is_selected_for_a_direct_render_target_sink()
    {
        var sink = new DirectRenderTargetSink();

        Assert.True(LibMpvCompositedPlaybackBackend.ShouldUseDirectRenderPath(sink));
    }

    [Fact]
    public void Copy_path_is_selected_for_a_plain_frame_sink()
    {
        // A sink that decodes into its own buffer (e.g. the LibVLC presenter) implements only
        // IPlaybackFrameSink and must never be routed through the zero-copy render-target path.
        var sink = new PlainFrameSink();

        Assert.False(LibMpvCompositedPlaybackBackend.ShouldUseDirectRenderPath(sink));
    }

    [Fact]
    public void Copy_path_is_selected_when_no_sink_is_attached()
    {
        Assert.False(LibMpvCompositedPlaybackBackend.ShouldUseDirectRenderPath(null));
    }

    private sealed class PlainFrameSink : IPlaybackFrameSink
    {
        public void OnVideoFormatChanged(VideoFrameDescriptor format)
        {
        }

        public void OnVideoFrameArrived(VideoFrame frame)
        {
        }

        public void OnVideoSurfaceCleared()
        {
        }
    }

    private sealed class DirectRenderTargetSink : IPlaybackFrameSink, IPlaybackDirectRenderTarget
    {
        public void OnVideoFormatChanged(VideoFrameDescriptor format)
        {
        }

        public void OnVideoFrameArrived(VideoFrame frame)
        {
        }

        public void OnVideoSurfaceCleared()
        {
        }

        public DirectRenderLock AcquireRenderLock() => default;
    }
}
