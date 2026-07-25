using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

/// <summary>
/// Deterministic coverage for the native render-buffer sizing and copy-length clamping that guard
/// against the resolution/format mismatch overread (BLOCKER B + I). These exercise the pure arithmetic
/// the render loop relies on; end-to-end coverage of the reallocation path lives in the native-gated
/// <see cref="CompositedPlaybackLiveTests"/>.
/// </summary>
public sealed class LibMpvCompositedPlaybackBufferTests
{
    [Fact]
    public void BufferOverread_copy_length_is_clamped_to_the_allocated_buffer()
    {
        // The frame requires more bytes than the native buffer holds (e.g. geometry advanced without a
        // reallocation). The managed copy must be clamped so it never reads past the allocation.
        int required = LibMpvCompositedPlaybackBackend.ComputeFrameBufferBytes(3840, 2160);
        int allocated = LibMpvCompositedPlaybackBackend.ComputeFrameBufferBytes(1280, 720);

        int copyLength = LibMpvCompositedPlaybackBackend.ClampFrameCopyLength(required, allocated);

        Assert.Equal(allocated, copyLength);
        Assert.True(copyLength < required);
    }

    [Fact]
    public void BufferOverread_copy_length_uses_full_frame_when_buffer_matches()
    {
        int bytes = LibMpvCompositedPlaybackBackend.ComputeFrameBufferBytes(1920, 1080);

        int copyLength = LibMpvCompositedPlaybackBackend.ClampFrameCopyLength(bytes, bytes);

        Assert.Equal(bytes, copyLength);
    }

    [Fact]
    public void BufferOverread_copy_length_is_never_negative_for_an_empty_buffer()
    {
        int required = LibMpvCompositedPlaybackBackend.ComputeFrameBufferBytes(1920, 1080);

        int copyLength = LibMpvCompositedPlaybackBackend.ClampFrameCopyLength(required, 0);

        Assert.Equal(0, copyLength);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void ResolutionChange_buffer_size_tracks_geometry(int width, int height)
    {
        int expected = checked(width * 4 * height);

        Assert.Equal(expected, LibMpvCompositedPlaybackBackend.ComputeFrameBufferBytes(width, height));
    }

    [Theory]
    [InlineData(-1, 720)]
    [InlineData(1280, -1)]
    [InlineData(-1, -1)]
    public void ResolutionChange_negative_geometry_is_clamped_to_zero(int width, int height)
    {
        Assert.Equal(0, LibMpvCompositedPlaybackBackend.ComputeFrameBufferBytes(width, height));
    }

    [Fact]
    public void ResolutionChange_growing_resolution_requires_a_larger_buffer_to_avoid_clamping()
    {
        // Before reallocation the old (stub) buffer is too small, so a full-resolution copy would be
        // clamped — precisely the overread the reallocation exists to prevent.
        int stubBuffer = LibMpvCompositedPlaybackBackend.ComputeFrameBufferBytes(1280, 720);
        int realFrame = LibMpvCompositedPlaybackBackend.ComputeFrameBufferBytes(3840, 2160);

        Assert.True(realFrame > stubBuffer);
        Assert.Equal(stubBuffer, LibMpvCompositedPlaybackBackend.ClampFrameCopyLength(realFrame, stubBuffer));

        // After reallocation the buffer matches the new geometry, so the whole frame copies with no clamp.
        int reallocatedBuffer = LibMpvCompositedPlaybackBackend.ComputeFrameBufferBytes(3840, 2160);

        Assert.Equal(realFrame, LibMpvCompositedPlaybackBackend.ClampFrameCopyLength(realFrame, reallocatedBuffer));
    }
}
