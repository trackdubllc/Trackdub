using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

/// <summary>
/// Exercises compositor frame delivery with a controlled render callback, without requiring a
/// machine-installed libmpv runtime. Runtime initialization remains covered by the live-gated tests.
/// </summary>
public sealed class LibMpvCompositedPlaybackBackendTests
{
    [Fact]
    public void Managed_sink_receives_frame_pixels_with_opaque_alpha()
    {
        using var fixture = new RenderFixture(renderResult: 0, width: 2, height: 1, stride: 8,
            [10, 20, 30, 0, 40, 50, 60, 0]);
        var sink = new CapturingSink();
        fixture.Backend.TryAttachFrameSink(sink);

        fixture.RenderFrame();

        Assert.Equal(new VideoFrameDescriptor(2, 1, 8, "bgr0"), sink.Format);
        VideoFrame frame = Assert.Single(sink.Frames);
        Assert.Equal([10, 20, 30, 255, 40, 50, 60, 255], frame.Data[..8]);
        Assert.Equal(1, fixture.RenderCallCount);
    }

    [Fact]
    public void Direct_render_writes_into_target_and_presents_the_frame()
    {
        using var fixture = new RenderFixture(renderResult: 0, width: 2, height: 1, stride: 8,
            [10, 20, 30, 0, 40, 50, 60, 0]);
        using var sink = new DirectRenderSink(
            width: 2,
            height: 1,
            stride: 8,
            [10, 20, 30, 0, 40, 50, 60, 0]);
        fixture.Backend.TryAttachFrameSink(sink);

        fixture.RenderFrame();

        Assert.Equal(new VideoFrameDescriptor(2, 1, 8, "bgr0"), sink.Format);
        Assert.Equal(1, sink.PresentCount);
        Assert.Empty(sink.Frames);
        Assert.Equal([10, 20, 30, 255, 40, 50, 60, 255], sink.ReadPixels());
        Assert.Equal(1, fixture.RenderCallCount);
    }

    [Fact]
    public void Direct_render_skips_when_the_presenter_has_no_back_buffer()
    {
        using var fixture = new RenderFixture(renderResult: 0, width: 2, height: 1, stride: 8,
            [10, 20, 30, 0, 40, 50, 60, 0]);
        using var sink = new DirectRenderSink(
            width: 2,
            height: 1,
            stride: 8,
            [10, 20, 30, 0, 40, 50, 60, 0],
            available: false);
        fixture.Backend.TryAttachFrameSink(sink);

        fixture.RenderFrame();

        Assert.Equal(0, sink.PresentCount);
        Assert.Empty(sink.Frames);
        Assert.Equal(0, fixture.RenderCallCount);
    }

    [Fact]
    public async Task Failed_native_render_does_not_publish_a_frame_and_exposes_warning()
    {
        using var fixture = new RenderFixture(renderResult: -4, width: 2, height: 1, stride: 8,
            [10, 20, 30, 0, 40, 50, 60, 0]);
        var sink = new CapturingSink();
        fixture.Backend.TryAttachFrameSink(sink);

        fixture.RenderFrame();
        PlaybackSnapshot snapshot = await fixture.Backend.GetSnapshotAsync(CancellationToken.None);

        Assert.Empty(sink.Frames);
        Assert.Contains("libmpv render returned error code -4", snapshot.WarningMessage);
        Assert.Equal(1, fixture.RenderCallCount);
    }

    private static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    private sealed class RenderFixture : IDisposable
    {
        private readonly Action onRender;
        private int renderCallCount;

        public RenderFixture(int renderResult, int width, int height, int stride, byte[] pixels)
        {
            Backend = new LibMpvCompositedPlaybackBackend("unused-native-library-for-render-unit-tests");
            onRender = () => renderCallCount++;

            SetField(Backend, "renderContext", new IntPtr(1));
            SetField(Backend, "renderPixelFormatPtr", Marshal.StringToHGlobalAnsi("bgr0"));
            SetField(Backend, "frameWidth", width);
            SetField(Backend, "frameHeight", height);
            SetField(Backend, "frameStride", stride);

            IntPtr frameBuffer = Marshal.AllocHGlobal(pixels.Length);
            SetField(Backend, "frameBuffer", frameBuffer);
            SetField(Backend, "frameBufferBytes", pixels.Length);
            Marshal.Copy(pixels, 0, frameBuffer, pixels.Length);

            IntPtr sizePointer = Marshal.AllocHGlobal(sizeof(int) * 2);
            SetField(Backend, "frameSizePtr", sizePointer);
            Marshal.WriteInt32(sizePointer, 0, width);
            Marshal.WriteInt32(sizePointer, sizeof(int), height);

            IntPtr stridePointer = Marshal.AllocHGlobal(IntPtr.Size);
            SetField(Backend, "frameStridePtr", stridePointer);
            Marshal.WriteIntPtr(stridePointer, new IntPtr(stride));

            Type callbackType = typeof(LibMpvCompositedPlaybackBackend).GetNestedType(
                "MpvRenderContextRenderFn",
                BindingFlags.NonPublic)!;
            ParameterExpression[] parameters = callbackType.GetMethod("Invoke")!
                .GetParameters()
                .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
                .ToArray();
            MethodInfo actionInvoke = typeof(Action).GetMethod(nameof(Action.Invoke))!;
            Expression body = Expression.Block(
                Expression.Call(Expression.Constant(onRender), actionInvoke),
                Expression.Constant(renderResult));
            Delegate callback = Expression.Lambda(callbackType, body, parameters).Compile();
            SetField(Backend, "mpv_render_context_render", callback);
        }

        public LibMpvCompositedPlaybackBackend Backend { get; }

        public int RenderCallCount => renderCallCount;

        public void RenderFrame() => typeof(LibMpvCompositedPlaybackBackend)
            .GetMethod("RenderCurrentFrame", InstancePrivate)!
            .Invoke(Backend, null);

        public void Dispose() => Backend.Dispose();

        private static void SetField<T>(LibMpvCompositedPlaybackBackend backend, string fieldName, T value) =>
            typeof(LibMpvCompositedPlaybackBackend)
                .GetField(fieldName, InstancePrivate)!
                .SetValue(backend, value);
    }

    private class CapturingSink : IPlaybackFrameSink
    {
        public VideoFrameDescriptor? Format { get; private set; }

        public List<VideoFrame> Frames { get; } = [];

        public void OnVideoFormatChanged(VideoFrameDescriptor format) => Format = format;

        public void OnVideoFrameArrived(VideoFrame frame) => Frames.Add(
            frame with { Data = frame.Data.ToArray() });

        public void OnVideoSurfaceCleared()
        {
        }
    }

    private sealed class DirectRenderSink : CapturingSink, IPlaybackDirectRenderTarget, IDisposable
    {
        private readonly int width;
        private readonly int height;
        private readonly int stride;
        private readonly IntPtr buffer;

        public DirectRenderSink(int width, int height, int stride, byte[] pixels, bool available = true)
        {
            this.width = width;
            this.height = height;
            this.stride = stride;
            buffer = available ? Marshal.AllocHGlobal(checked(stride * height)) : IntPtr.Zero;
            if (buffer != IntPtr.Zero)
            {
                Marshal.Copy(pixels, 0, buffer, pixels.Length);
            }
        }

        public int PresentCount { get; private set; }

        public DirectRenderLock AcquireRenderLock() => buffer == IntPtr.Zero
            ? default
            : new DirectRenderLock(buffer, width, height, stride, () => PresentCount++);

        public byte[] ReadPixels()
        {
            byte[] pixels = new byte[checked(stride * height)];
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
            return pixels;
        }

        public void Dispose()
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
